using System.Text.Json;
using System.Text.Json.Serialization;

namespace EconGrader.Web.Converters;

/// <summary>
/// Serializes UTC <see cref="DateTime"/> values as Iran local time
/// (Asia/Tehran: UTC+3:30 in standard time, UTC+4:30 during Iran daylight
/// saving) so the API never exposes raw GMT/UTC wall-clock times.
/// </summary>
/// <remarks>
/// <para>
/// <b>The bug this fixes.</b> Every timestamp in the domain is stored as UTC —
/// entity defaults use <c>DateTime.UtcNow</c>, <see cref="HealthController"/>
/// returns <c>DateTime.UtcNow</c>, and SQL seed scripts use <c>GETUTCDATE()</c>.
/// EF Core reads SQL Server <c>datetime2</c> columns back into C#
/// <see cref="DateTime"/> values with <see cref="DateTimeKind.Unspecified"/>
/// (the column has no offset to restore a Kind from). System.Text.Json
/// serializes an <c>Unspecified</c> <see cref="DateTime"/> <i>without any
/// offset</i> — i.e. a bare wall-clock string such as
/// <c>2026-08-26T10:30:00</c>. JavaScript then parses that offset-less string
/// as <i>local</i> time, so the stored UTC instant is shifted by the host's
/// offset (≈ 3.5h for Iran) and every "created/logged now" value renders as
/// "3 hours ago" / GMT. There was previously no <see cref="DateTimeKind"/>
/// handling or <see cref="JsonConverter"/> anywhere in the codebase.
/// </para>
/// <para>
/// This converter normalizes each value to a UTC instant and emits it as Iran
/// local time <b>with an explicit offset</b> (e.g.
/// <c>2026-08-26T14:00:00+03:30</c>), which round-trips correctly on every
/// client. Storage stays UTC; only the serialized representation changes, so no
/// database migration is required. The offset comes from the OS/IANA time-zone
/// database through <see cref="TimeZoneInfo"/> (Iran uses a fixed UTC+03:30
/// since abolishing DST in 2022), and a fixed-offset fallback covers hosts
/// without a tz database. Deriving from <see cref="JsonConverter{T}"/> means
/// System.Text.Json applies it to both <c>DateTime</c> and <c>DateTime?</c>
/// members.
/// </para>
/// </remarks>
public sealed class IranDateTimeJsonConverter : JsonConverter<DateTime>
{
    private static readonly TimeZoneInfo IranTimeZone = LoadIranTimeZone();

    private static TimeZoneInfo LoadIranTimeZone()
    {
        // IANA id on Linux/Docker, Windows display id on Windows dev boxes.
        foreach (var id in new[] { "Asia/Tehran", "Iran Standard Time", "Iran" })
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
            catch (TimeZoneNotFoundException) { /* try next id */ }
            catch (InvalidTimeZoneException) { /* try next id */ }
        }
        // Fallback when no tz database is present: fixed UTC+3:30, no DST.
        return TimeZoneInfo.CreateCustomTimeZone(
            "Iran", TimeSpan.FromHours(3) + TimeSpan.FromMinutes(30),
            "Iran Time", "IRST");
    }

    private static DateTime ToUtcInstant(DateTime value) =>
        value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            // DB reads come back as Unspecified but store UTC, so treat the
            // wall-clock as UTC rather than as the server's local time.
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
        };

    public override void Write(
        Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options)
    {
        var utc = ToUtcInstant(value);
        var iranLocal = TimeZoneInfo.ConvertTimeFromUtc(utc, IranTimeZone);
        var dto = new DateTimeOffset(iranLocal, IranTimeZone.GetUtcOffset(iranLocal));

        // "O" round-trip format keeps the explicit offset (e.g.
        // 2026-08-28T14:00:00+03:30), which every client parses unambiguously.
        // DateTimeOffset is not matched by this JsonConverter<DateTime>, so
        // there is no recursion risk.
        writer.WriteStringValue(dto.ToString("O"));
    }

    public override DateTime Read(
        ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        // No API endpoint sends DateTimes in a JSON body (request timestamps are
        // parsed from query strings via MVC model binding, not this serializer).
        // Kept correct so the converter round-trips safely: parse -> normalize
        // to a UTC instant.
        if (reader.TokenType != JsonTokenType.String)
            throw new JsonException(
                $"Unexpected token when reading a DateTime: {reader.TokenType}.");

        var raw = reader.GetString()!;
        if (!DateTime.TryParse(raw, out var parsed))
            throw new JsonException($"Cannot parse '{raw}' as a DateTime.");

        return parsed.Kind switch
        {
            DateTimeKind.Utc => parsed,
            DateTimeKind.Local => parsed.ToUniversalTime(),
            _ => DateTime.SpecifyKind(parsed, DateTimeKind.Utc),
        };
    }
}
