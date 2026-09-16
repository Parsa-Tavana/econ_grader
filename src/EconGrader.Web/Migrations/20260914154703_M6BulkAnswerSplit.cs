using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EconGrader.Web.Migrations
{
    /// <inheritdoc />
    public partial class M6BulkAnswerSplit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BulkAnswerBatches",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    QuestionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SourceArtifactId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SourceFileName = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    SourceContentType = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Status = table.Column<string>(type: "nvarchar(24)", maxLength: 24, nullable: false),
                    TotalPages = table.Column<int>(type: "int", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Attempts = table.Column<int>(type: "int", nullable: false),
                    ErrorKind = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    Error = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    LeaseToken = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    LeaseUntil = table.Column<DateTime>(type: "datetime2", nullable: true),
                    StartedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    FinishedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BulkAnswerBatches", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BulkAnswerBatches_Artifacts_SourceArtifactId",
                        column: x => x.SourceArtifactId,
                        principalTable: "Artifacts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_BulkAnswerBatches_Questions_QuestionId",
                        column: x => x.QuestionId,
                        principalTable: "Questions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "BulkPageMappings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BatchId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PageArtifactId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PageNumber = table.Column<int>(type: "int", nullable: false),
                    RawOcrId = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    OcrConfidence = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    MatchedStudentId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ReviewStatus = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    SortInStack = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BulkPageMappings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BulkPageMappings_Artifacts_PageArtifactId",
                        column: x => x.PageArtifactId,
                        principalTable: "Artifacts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BulkPageMappings_BulkAnswerBatches_BatchId",
                        column: x => x.BatchId,
                        principalTable: "BulkAnswerBatches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_BulkPageMappings_Students_MatchedStudentId",
                        column: x => x.MatchedStudentId,
                        principalTable: "Students",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BulkAnswerBatches_CreatedAt",
                table: "BulkAnswerBatches",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_BulkAnswerBatches_QuestionId_SourceArtifactId",
                table: "BulkAnswerBatches",
                columns: new[] { "QuestionId", "SourceArtifactId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BulkAnswerBatches_SourceArtifactId",
                table: "BulkAnswerBatches",
                column: "SourceArtifactId");

            migrationBuilder.CreateIndex(
                name: "IX_BulkAnswerBatches_Status_LeaseUntil",
                table: "BulkAnswerBatches",
                columns: new[] { "Status", "LeaseUntil" });

            migrationBuilder.CreateIndex(
                name: "IX_BulkPageMappings_BatchId_MatchedStudentId",
                table: "BulkPageMappings",
                columns: new[] { "BatchId", "MatchedStudentId" });

            migrationBuilder.CreateIndex(
                name: "IX_BulkPageMappings_BatchId_PageNumber",
                table: "BulkPageMappings",
                columns: new[] { "BatchId", "PageNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BulkPageMappings_MatchedStudentId",
                table: "BulkPageMappings",
                column: "MatchedStudentId");

            migrationBuilder.CreateIndex(
                name: "IX_BulkPageMappings_PageArtifactId",
                table: "BulkPageMappings",
                column: "PageArtifactId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BulkPageMappings");

            migrationBuilder.DropTable(
                name: "BulkAnswerBatches");
        }
    }
}
