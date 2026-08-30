using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EconGrader.Web.Migrations
{
    /// <summary>
    /// Adds optional file attachments to Question and Rubric, and
    /// FileName/ContentType metadata to Answer. Purely additive — no data loss.
    /// </summary>
    public partial class AddQuestionRubricAnswerFiles : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "FileStorageKey",
                table: "Questions",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FileName",
                table: "Questions",
                type: "nvarchar(260)",
                maxLength: 260,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ContentType",
                table: "Questions",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FileStorageKey",
                table: "Rubrics",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FileName",
                table: "Rubrics",
                type: "nvarchar(260)",
                maxLength: 260,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ContentType",
                table: "Rubrics",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FileName",
                table: "Answers",
                type: "nvarchar(260)",
                maxLength: 260,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ContentType",
                table: "Answers",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "FileStorageKey", table: "Questions");
            migrationBuilder.DropColumn(name: "FileName", table: "Questions");
            migrationBuilder.DropColumn(name: "ContentType", table: "Questions");

            migrationBuilder.DropColumn(name: "FileStorageKey", table: "Rubrics");
            migrationBuilder.DropColumn(name: "FileName", table: "Rubrics");
            migrationBuilder.DropColumn(name: "ContentType", table: "Rubrics");

            migrationBuilder.DropColumn(name: "FileName", table: "Answers");
            migrationBuilder.DropColumn(name: "ContentType", table: "Answers");
        }
    }
}