using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace TicTacToe.Data.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20261102000000_RemoveLocalAccounts")]
public sealed class RemoveLocalAccounts : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropTable(name: "Accounts");

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "Accounts",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                PlayerId = table.Column<Guid>(type: "TEXT", nullable: false),
                Login = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                PasswordHash = table.Column<string>(type: "TEXT", nullable: false),
                PasswordSalt = table.Column<string>(type: "TEXT", nullable: false),
                CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Accounts", x => x.Id);
                table.ForeignKey("FK_Accounts_Players_PlayerId", x => x.PlayerId, "Players", "Id", onDelete: ReferentialAction.Cascade);
            });
        migrationBuilder.CreateIndex("IX_Accounts_Login", "Accounts", "Login", unique: true);
        migrationBuilder.CreateIndex("IX_Accounts_PlayerId", "Accounts", "PlayerId", unique: true);
    }

    protected override void BuildTargetModel(Microsoft.EntityFrameworkCore.ModelBuilder modelBuilder) =>
        AppDbContext.ConfigureModel(modelBuilder, includeAccounts: false);
}
