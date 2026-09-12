using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EconGrader.Web.Migrations
{
    /// <inheritdoc />
    public partial class ExamYearToExamDate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Add the date column (existing rows get a placeholder, then are
            // backfilled from Year), then drop the old column — no data lost.
            migrationBuilder.AddColumn<DateTime>(
                name: "ExamDate",
                table: "Exams",
                type: "date",
                nullable: false,
                defaultValue: new DateTime(1900, 1, 1));

            migrationBuilder.Sql(@"
                UPDATE Exams SET ExamDate = CAST(CAST(Year AS nvarchar(4)) + '-01-01' AS date);
            ");

            migrationBuilder.DropColumn(
                name: "Year",
                table: "Exams");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Year",
                table: "Exams",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.Sql(@"
                UPDATE Exams SET Year = YEAR(ExamDate);
            ");

            migrationBuilder.DropColumn(
                name: "ExamDate",
                table: "Exams");
        }
    }
}
