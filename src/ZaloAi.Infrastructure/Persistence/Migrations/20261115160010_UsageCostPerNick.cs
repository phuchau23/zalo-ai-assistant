using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ZaloAi.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class UsageCostPerNick : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "connection_id",
                table: "usage_records",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "exchange_rates",
                columns: table => new
                {
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    date = table.Column<DateOnly>(type: "date", nullable: false),
                    sell = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    transfer = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    source = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    fetched_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_exchange_rates", x => new { x.currency, x.date });
                });

            migrationBuilder.CreateIndex(
                name: "ix_usage_records_tenant_id_connection_id_created_at",
                table: "usage_records",
                columns: new[] { "tenant_id", "connection_id", "created_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "exchange_rates");

            migrationBuilder.DropIndex(
                name: "ix_usage_records_tenant_id_connection_id_created_at",
                table: "usage_records");

            migrationBuilder.DropColumn(
                name: "connection_id",
                table: "usage_records");
        }
    }
}
