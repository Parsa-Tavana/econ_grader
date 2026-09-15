using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EconGrader.Web.Migrations
{
    /// <inheritdoc />
    public partial class M1ArtifactsItemBanking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "AnswerKeyArtifactId",
                table: "Questions",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AnswerKeyContentType",
                table: "Questions",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AnswerKeyFileName",
                table: "Questions",
                type: "nvarchar(260)",
                maxLength: 260,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AnswerKeyStorageKey",
                table: "Questions",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "FileArtifactId",
                table: "Questions",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "InputArtifactsJson",
                table: "GradingRuns",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "GroundTruthGradingEnabled",
                table: "Exams",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "RubricFileArtifactId",
                table: "Exams",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "OriginalArtifactId",
                table: "Answers",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Artifacts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    StorageKey = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Sha256 = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Bytes = table.Column<long>(type: "bigint", nullable: false),
                    Width = table.Column<int>(type: "int", nullable: true),
                    Height = table.Column<int>(type: "int", nullable: true),
                    PageNumber = table.Column<int>(type: "int", nullable: true),
                    Format = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: true),
                    TextContent = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ParentArtifactId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    PageCount = table.Column<int>(type: "int", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Artifacts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Artifacts_Artifacts_ParentArtifactId",
                        column: x => x.ParentArtifactId,
                        principalTable: "Artifacts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "AnswerPages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AnswerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ArtifactId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Format = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    SortOrder = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AnswerPages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AnswerPages_Answers_AnswerId",
                        column: x => x.AnswerId,
                        principalTable: "Answers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AnswerPages_Artifacts_ArtifactId",
                        column: x => x.ArtifactId,
                        principalTable: "Artifacts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "IngestJobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OriginalArtifactId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Attempts = table.Column<int>(type: "int", nullable: false),
                    ErrorKind = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    Error = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    StartedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    FinishedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LeaseToken = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    LeaseUntil = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Role = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IngestJobs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_IngestJobs_Artifacts_OriginalArtifactId",
                        column: x => x.OriginalArtifactId,
                        principalTable: "Artifacts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "QuestionAnswerKeys",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    QuestionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ArtifactId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SortOrder = table.Column<int>(type: "int", nullable: false),
                    Role = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QuestionAnswerKeys", x => x.Id);
                    table.ForeignKey(
                        name: "FK_QuestionAnswerKeys_Artifacts_ArtifactId",
                        column: x => x.ArtifactId,
                        principalTable: "Artifacts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_QuestionAnswerKeys_Questions_QuestionId",
                        column: x => x.QuestionId,
                        principalTable: "Questions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "QuestionAssets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    QuestionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ArtifactId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SortOrder = table.Column<int>(type: "int", nullable: false),
                    Role = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QuestionAssets", x => x.Id);
                    table.ForeignKey(
                        name: "FK_QuestionAssets_Artifacts_ArtifactId",
                        column: x => x.ArtifactId,
                        principalTable: "Artifacts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_QuestionAssets_Questions_QuestionId",
                        column: x => x.QuestionId,
                        principalTable: "Questions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Questions_AnswerKeyArtifactId",
                table: "Questions",
                column: "AnswerKeyArtifactId");

            migrationBuilder.CreateIndex(
                name: "IX_Questions_FileArtifactId",
                table: "Questions",
                column: "FileArtifactId");

            migrationBuilder.CreateIndex(
                name: "IX_Exams_RubricFileArtifactId",
                table: "Exams",
                column: "RubricFileArtifactId");

            migrationBuilder.CreateIndex(
                name: "IX_Answers_OriginalArtifactId",
                table: "Answers",
                column: "OriginalArtifactId");

            migrationBuilder.CreateIndex(
                name: "IX_AnswerPages_AnswerId_Format_SortOrder",
                table: "AnswerPages",
                columns: new[] { "AnswerId", "Format", "SortOrder" });

            migrationBuilder.CreateIndex(
                name: "IX_AnswerPages_ArtifactId",
                table: "AnswerPages",
                column: "ArtifactId");

            migrationBuilder.CreateIndex(
                name: "IX_Artifacts_ParentArtifactId",
                table: "Artifacts",
                column: "ParentArtifactId");

            migrationBuilder.CreateIndex(
                name: "IX_Artifacts_Sha256_Kind",
                table: "Artifacts",
                columns: new[] { "Sha256", "Kind" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Artifacts_StorageKey",
                table: "Artifacts",
                column: "StorageKey");

            migrationBuilder.CreateIndex(
                name: "IX_IngestJobs_OriginalArtifactId_Status",
                table: "IngestJobs",
                columns: new[] { "OriginalArtifactId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_IngestJobs_Status_LeaseUntil",
                table: "IngestJobs",
                columns: new[] { "Status", "LeaseUntil" });

            migrationBuilder.CreateIndex(
                name: "IX_QuestionAnswerKeys_ArtifactId",
                table: "QuestionAnswerKeys",
                column: "ArtifactId");

            migrationBuilder.CreateIndex(
                name: "IX_QuestionAnswerKeys_QuestionId_SortOrder",
                table: "QuestionAnswerKeys",
                columns: new[] { "QuestionId", "SortOrder" });

            migrationBuilder.CreateIndex(
                name: "IX_QuestionAssets_ArtifactId",
                table: "QuestionAssets",
                column: "ArtifactId");

            migrationBuilder.CreateIndex(
                name: "IX_QuestionAssets_QuestionId_SortOrder",
                table: "QuestionAssets",
                columns: new[] { "QuestionId", "SortOrder" });

            migrationBuilder.AddForeignKey(
                name: "FK_Answers_Artifacts_OriginalArtifactId",
                table: "Answers",
                column: "OriginalArtifactId",
                principalTable: "Artifacts",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Exams_Artifacts_RubricFileArtifactId",
                table: "Exams",
                column: "RubricFileArtifactId",
                principalTable: "Artifacts",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Questions_Artifacts_AnswerKeyArtifactId",
                table: "Questions",
                column: "AnswerKeyArtifactId",
                principalTable: "Artifacts",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Questions_Artifacts_FileArtifactId",
                table: "Questions",
                column: "FileArtifactId",
                principalTable: "Artifacts",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Answers_Artifacts_OriginalArtifactId",
                table: "Answers");

            migrationBuilder.DropForeignKey(
                name: "FK_Exams_Artifacts_RubricFileArtifactId",
                table: "Exams");

            migrationBuilder.DropForeignKey(
                name: "FK_Questions_Artifacts_AnswerKeyArtifactId",
                table: "Questions");

            migrationBuilder.DropForeignKey(
                name: "FK_Questions_Artifacts_FileArtifactId",
                table: "Questions");

            migrationBuilder.DropTable(
                name: "AnswerPages");

            migrationBuilder.DropTable(
                name: "IngestJobs");

            migrationBuilder.DropTable(
                name: "QuestionAnswerKeys");

            migrationBuilder.DropTable(
                name: "QuestionAssets");

            migrationBuilder.DropTable(
                name: "Artifacts");

            migrationBuilder.DropIndex(
                name: "IX_Questions_AnswerKeyArtifactId",
                table: "Questions");

            migrationBuilder.DropIndex(
                name: "IX_Questions_FileArtifactId",
                table: "Questions");

            migrationBuilder.DropIndex(
                name: "IX_Exams_RubricFileArtifactId",
                table: "Exams");

            migrationBuilder.DropIndex(
                name: "IX_Answers_OriginalArtifactId",
                table: "Answers");

            migrationBuilder.DropColumn(
                name: "AnswerKeyArtifactId",
                table: "Questions");

            migrationBuilder.DropColumn(
                name: "AnswerKeyContentType",
                table: "Questions");

            migrationBuilder.DropColumn(
                name: "AnswerKeyFileName",
                table: "Questions");

            migrationBuilder.DropColumn(
                name: "AnswerKeyStorageKey",
                table: "Questions");

            migrationBuilder.DropColumn(
                name: "FileArtifactId",
                table: "Questions");

            migrationBuilder.DropColumn(
                name: "InputArtifactsJson",
                table: "GradingRuns");

            migrationBuilder.DropColumn(
                name: "GroundTruthGradingEnabled",
                table: "Exams");

            migrationBuilder.DropColumn(
                name: "RubricFileArtifactId",
                table: "Exams");

            migrationBuilder.DropColumn(
                name: "OriginalArtifactId",
                table: "Answers");
        }
    }
}
