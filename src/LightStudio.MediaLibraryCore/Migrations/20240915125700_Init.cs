using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LightStudio.MediaLibraryCore.Migrations
{
    /// <inheritdoc />
    public partial class Init : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MediaFiles",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Duration = table.Column<TimeSpan>(type: "TEXT", nullable: false),
                    TotalDiscs = table.Column<string>(type: "TEXT", nullable: true),
                    DiscNumber = table.Column<string>(type: "TEXT", nullable: true),
                    TotalTracks = table.Column<string>(type: "TEXT", nullable: true),
                    Description = table.Column<string>(type: "TEXT", nullable: true),
                    Copyright = table.Column<string>(type: "TEXT", nullable: true),
                    Comments = table.Column<string>(type: "TEXT", nullable: true),
                    Grouping = table.Column<string>(type: "TEXT", nullable: true),
                    Genre = table.Column<string>(type: "TEXT", nullable: true),
                    TrackNumber = table.Column<string>(type: "TEXT", nullable: true),
                    AlbumArtist = table.Column<string>(type: "TEXT", nullable: true),
                    Performer = table.Column<string>(type: "TEXT", nullable: true),
                    Composer = table.Column<string>(type: "TEXT", nullable: true),
                    Date = table.Column<string>(type: "TEXT", nullable: true),
                    Album = table.Column<string>(type: "TEXT", nullable: true),
                    Artist = table.Column<string>(type: "TEXT", nullable: true),
                    Title = table.Column<string>(type: "TEXT", nullable: true),
                    Path = table.Column<string>(type: "TEXT", nullable: true),
                    DatabaseItemAddedDate = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    FileLastModifiedDate = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    StartTime = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MediaFiles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PlaybackHistory",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    RelatedMediaFileId = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlaybackHistory", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PlaybackHistory_MediaFiles_RelatedMediaFileId",
                        column: x => x.RelatedMediaFileId,
                        principalTable: "MediaFiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PlaybackHistory_RelatedMediaFileId",
                table: "PlaybackHistory",
                column: "RelatedMediaFileId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PlaybackHistory");

            migrationBuilder.DropTable(
                name: "MediaFiles");
        }
    }
}
