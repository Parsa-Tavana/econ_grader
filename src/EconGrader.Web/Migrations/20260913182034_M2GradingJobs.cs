using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EconGrader.Web.Migrations
{
    /// <inheritdoc />
    public partial class M2GradingJobs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "GradingJobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AnswerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Temperature = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    PromptVersion = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    EnsembleIndex = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    Attempts = table.Column<int>(type: "int", nullable: false),
                    ErrorKind = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Error = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    StartedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    FinishedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LeaseToken = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    LeaseUntil = table.Column<DateTime>(type: "datetime2", nullable: true),
                    GradingRunId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GradingJobs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GradingJobs_GradingRuns_GradingRunId",
                        column: x => x.GradingRunId,
                        principalTable: "GradingRuns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_GradingJobs_AnswerId_Temperature_PromptVersion_EnsembleIndex",
                table: "GradingJobs",
                columns: new[] { "AnswerId", "Temperature", "PromptVersion", "EnsembleIndex" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GradingJobs_CreatedAt",
                table: "GradingJobs",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_GradingJobs_GradingRunId",
                table: "GradingJobs",
                column: "GradingRunId");

            migrationBuilder.CreateIndex(
                name: "IX_GradingJobs_Status_LeaseUntil",
                table: "GradingJobs",
                columns: new[] { "Status", "LeaseUntil" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "GradingJobs");
        }
    }
}
