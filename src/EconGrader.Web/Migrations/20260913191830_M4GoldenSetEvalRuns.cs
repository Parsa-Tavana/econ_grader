using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EconGrader.Web.Migrations
{
    /// <inheritdoc />
    public partial class M4GoldenSetEvalRuns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "EvalRuns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    QuestionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ConfigurationJson = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    BaselineQwk = table.Column<decimal>(type: "decimal(18,2)", nullable: true),
                    BaselineMae = table.Column<decimal>(type: "decimal(18,2)", nullable: true),
                    BaselineExactAgreementPct = table.Column<decimal>(type: "decimal(18,2)", nullable: true),
                    BaselineCount = table.Column<int>(type: "int", nullable: false),
                    CandidateQwk = table.Column<decimal>(type: "decimal(18,2)", nullable: true),
                    CandidateMae = table.Column<decimal>(type: "decimal(18,2)", nullable: true),
                    CandidateExactAgreementPct = table.Column<decimal>(type: "decimal(18,2)", nullable: true),
                    CandidateCount = table.Column<int>(type: "int", nullable: false),
                    QwkDelta = table.Column<decimal>(type: "decimal(18,2)", nullable: true),
                    Regressed = table.Column<bool>(type: "bit", nullable: false),
                    DetailsJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CreatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EvalRuns", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EvalRuns_Questions_QuestionId",
                        column: x => x.QuestionId,
                        principalTable: "Questions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_EvalRuns_CreatedAt",
                table: "EvalRuns",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_EvalRuns_QuestionId",
                table: "EvalRuns",
                column: "QuestionId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EvalRuns");
        }
    }
}
