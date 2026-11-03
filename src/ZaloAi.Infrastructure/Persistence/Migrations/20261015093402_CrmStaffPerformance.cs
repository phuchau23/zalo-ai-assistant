using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ZaloAi.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CrmStaffPerformance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "attendance",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    staff_member_id = table.Column<Guid>(type: "uuid", nullable: false),
                    branch_id = table.Column<Guid>(type: "uuid", nullable: false),
                    clock_in_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    clock_out_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    source = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    note = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    by_user_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_attendance", x => x.id);
                    table.ForeignKey(
                        name: "fk_attendance_branches_branch_id",
                        column: x => x.branch_id,
                        principalTable: "branches",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_attendance_staff_members_staff_member_id",
                        column: x => x.staff_member_id,
                        principalTable: "staff_members",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_attendance_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "commission_entries",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    staff_member_id = table.Column<Guid>(type: "uuid", nullable: false),
                    invoice_id = table.Column<Guid>(type: "uuid", nullable: true),
                    invoice_line_id = table.Column<Guid>(type: "uuid", nullable: true),
                    redemption_id = table.Column<Guid>(type: "uuid", nullable: true),
                    rule_id = table.Column<Guid>(type: "uuid", nullable: true),
                    basis = table.Column<long>(type: "bigint", nullable: false),
                    amount = table.Column<long>(type: "bigint", nullable: false),
                    period = table.Column<string>(type: "character varying(7)", maxLength: 7, nullable: false),
                    at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    reverted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    paid_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_commission_entries", x => x.id);
                    table.ForeignKey(
                        name: "fk_commission_entries_invoices_invoice_id",
                        column: x => x.invoice_id,
                        principalTable: "invoices",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_commission_entries_staff_members_staff_member_id",
                        column: x => x.staff_member_id,
                        principalTable: "staff_members",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_commission_entries_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "commission_rules",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    staff_member_id = table.Column<Guid>(type: "uuid", nullable: true),
                    scope = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    catalog_item_id = table.Column<Guid>(type: "uuid", nullable: true),
                    kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    value = table.Column<long>(type: "bigint", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    note = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_commission_rules", x => x.id);
                    table.ForeignKey(
                        name: "fk_commission_rules_catalog_items_catalog_item_id",
                        column: x => x.catalog_item_id,
                        principalTable: "catalog_items",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_commission_rules_staff_members_staff_member_id",
                        column: x => x.staff_member_id,
                        principalTable: "staff_members",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_commission_rules_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_attendance_branch_id",
                table: "attendance",
                column: "branch_id");

            migrationBuilder.CreateIndex(
                name: "ix_attendance_staff_member_id",
                table: "attendance",
                column: "staff_member_id");

            migrationBuilder.CreateIndex(
                name: "ix_attendance_tenant_id_staff_member_id_clock_in_at",
                table: "attendance",
                columns: new[] { "tenant_id", "staff_member_id", "clock_in_at" });

            migrationBuilder.CreateIndex(
                name: "ix_commission_entries_invoice_id",
                table: "commission_entries",
                column: "invoice_id");

            migrationBuilder.CreateIndex(
                name: "ix_commission_entries_staff_member_id",
                table: "commission_entries",
                column: "staff_member_id");

            migrationBuilder.CreateIndex(
                name: "ix_commission_entries_tenant_id_invoice_id",
                table: "commission_entries",
                columns: new[] { "tenant_id", "invoice_id" });

            migrationBuilder.CreateIndex(
                name: "ix_commission_entries_tenant_id_staff_member_id_period",
                table: "commission_entries",
                columns: new[] { "tenant_id", "staff_member_id", "period" });

            migrationBuilder.CreateIndex(
                name: "ix_commission_rules_catalog_item_id",
                table: "commission_rules",
                column: "catalog_item_id");

            migrationBuilder.CreateIndex(
                name: "ix_commission_rules_staff_member_id",
                table: "commission_rules",
                column: "staff_member_id");

            migrationBuilder.CreateIndex(
                name: "ix_commission_rules_tenant_id_staff_member_id",
                table: "commission_rules",
                columns: new[] { "tenant_id", "staff_member_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "attendance");

            migrationBuilder.DropTable(
                name: "commission_entries");

            migrationBuilder.DropTable(
                name: "commission_rules");
        }
    }
}
