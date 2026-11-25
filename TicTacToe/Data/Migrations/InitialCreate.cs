using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace TicTacToe.Data.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20261027000000_InitialCreate")]
public sealed class InitialCreate : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "Players",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                Name = table.Column<string>(type: "TEXT", maxLength: 24, nullable: false),
                CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_Players", x => x.Id));

        migrationBuilder.CreateTable(
            name: "Games",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                RoomId = table.Column<string>(type: "TEXT", nullable: true),
                PlayerXId = table.Column<Guid>(type: "TEXT", nullable: false),
                PlayerOId = table.Column<Guid>(type: "TEXT", nullable: false),
                WinnerId = table.Column<Guid>(type: "TEXT", nullable: true),
                Result = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                ConnectionMode = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                StartedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                FinishedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                FinalBoard = table.Column<string>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Games", x => x.Id);
                table.ForeignKey("FK_Games_Players_PlayerOId", x => x.PlayerOId, "Players", "Id", onDelete: ReferentialAction.Restrict);
                table.ForeignKey("FK_Games_Players_PlayerXId", x => x.PlayerXId, "Players", "Id", onDelete: ReferentialAction.Restrict);
                table.ForeignKey("FK_Games_Players_WinnerId", x => x.WinnerId, "Players", "Id", onDelete: ReferentialAction.SetNull);
            });

        migrationBuilder.CreateTable(
            name: "Moves",
            columns: table => new
            {
                Id = table.Column<long>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                GameId = table.Column<Guid>(type: "TEXT", nullable: false),
                PlayerId = table.Column<Guid>(type: "TEXT", nullable: false),
                CellIndex = table.Column<int>(type: "INTEGER", nullable: false),
                MoveNumber = table.Column<int>(type: "INTEGER", nullable: false),
                CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Moves", x => x.Id);
                table.CheckConstraint("CK_Moves_CellIndex", "CellIndex >= 0 AND CellIndex <= 8");
                table.ForeignKey("FK_Moves_Games_GameId", x => x.GameId, "Games", "Id", onDelete: ReferentialAction.Cascade);
                table.ForeignKey("FK_Moves_Players_PlayerId", x => x.PlayerId, "Players", "Id", onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateIndex("IX_Games_PlayerOId", "Games", "PlayerOId");
        migrationBuilder.CreateIndex("IX_Games_PlayerXId", "Games", "PlayerXId");
        migrationBuilder.CreateIndex("IX_Games_RoomId", "Games", "RoomId", unique: true);
        migrationBuilder.CreateIndex("IX_Games_WinnerId", "Games", "WinnerId");
        migrationBuilder.CreateIndex("IX_Moves_GameId_MoveNumber", "Moves", new[] { "GameId", "MoveNumber" }, unique: true);
        migrationBuilder.CreateIndex("IX_Moves_PlayerId", "Moves", "PlayerId");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable("Moves");
        migrationBuilder.DropTable("Games");
        migrationBuilder.DropTable("Players");
    }

    protected override void BuildTargetModel(Microsoft.EntityFrameworkCore.ModelBuilder modelBuilder) =>
        AppDbContext.ConfigureModel(modelBuilder, includeAccounts: false);
}
