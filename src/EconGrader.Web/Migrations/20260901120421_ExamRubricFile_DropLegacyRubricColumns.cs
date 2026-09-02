using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EconGrader.Web.Migrations
{
    /// <inheritdoc />
    public partial class ExamRubricFile_DropLegacyRubricColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ContentType",
                table: "Rubrics");

            migrationBuilder.DropColumn(
                name: "FileName",
                table: "Rubrics");

            migrationBuilder.DropColumn(
                name: "FileStorageKey",
                table: "Rubrics");

            migrationBuilder.DropColumn(
                name: "RubricText",
                table: "Questions");

            migrationBuilder.AddColumn<string>(
                name: "RubricFileContentType",
                table: "Exams",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RubricFileName",
                table: "Exams",
                type: "nvarchar(260)",
                maxLength: 260,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RubricFileStorageKey",
                table: "Exams",
                type: "nvarchar(max)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RubricFileContentType",
                table: "Exams");

            migrationBuilder.DropColumn(
                name: "RubricFileName",
                table: "Exams");

            migrationBuilder.DropColumn(
                name: "RubricFileStorageKey",
                table: "Exams");

            migrationBuilder.AddColumn<string>(
                name: "ContentType",
                table: "Rubrics",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FileName",
                table: "Rubrics",
                type: "nvarchar(260)",
                maxLength: 260,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FileStorageKey",
                table: "Rubrics",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RubricText",
                table: "Questions",
                type: "nvarchar(max)",
                nullable: true);
        }
    }
}
