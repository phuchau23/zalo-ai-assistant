using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ZaloAi.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ZaloPersonalOutreach : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "origin",
                table: "contacts",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "referred_by_contact_id",
                table: "contacts",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "outreach_batches",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    connection_id = table.Column<Guid>(type: "uuid", nullable: true),
                    origin = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    note_enc = table.Column<string>(type: "text", nullable: true),
                    referrer_contact_id = table.Column<Guid>(type: "uuid", nullable: true),
                    referrer_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    attested_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    attested_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    accepted_count = table.Column<int>(type: "integer", nullable: false),
                    skipped_count = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_outreach_batches", x => x.id);
                    table.ForeignKey(
                        name: "fk_outreach_batches_channel_connections_connection_id",
                        column: x => x.connection_id,
                        principalTable: "channel_connections",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_outreach_batches_contacts_referrer_contact_id",
                        column: x => x.referrer_contact_id,
                        principalTable: "contacts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_outreach_batches_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_outreach_batches_users_attested_by_user_id",
                        column: x => x.attested_by_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "outreach_targets",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    batch_id = table.Column<Guid>(type: "uuid", nullable: false),
                    contact_id = table.Column<Guid>(type: "uuid", nullable: false),
                    connection_id = table.Column<Guid>(type: "uuid", nullable: true),
                    assigned_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    state = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    next_action_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    zalo_uid = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    friend_request_sent_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    first_message_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    replied_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_error = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_outreach_targets", x => x.id);
                    table.ForeignKey(
                        name: "fk_outreach_targets_channel_connections_connection_id",
                        column: x => x.connection_id,
                        principalTable: "channel_connections",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_outreach_targets_contacts_contact_id",
                        column: x => x.contact_id,
                        principalTable: "contacts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_outreach_targets_outreach_batches_batch_id",
                        column: x => x.batch_id,
                        principalTable: "outreach_batches",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_outreach_targets_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_outreach_targets_users_assigned_user_id",
                        column: x => x.assigned_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "ix_contacts_referred_by_contact_id",
                table: "contacts",
                column: "referred_by_contact_id");

            migrationBuilder.CreateIndex(
                name: "ix_outreach_batches_attested_by_user_id",
                table: "outreach_batches",
                column: "attested_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_outreach_batches_connection_id",
                table: "outreach_batches",
                column: "connection_id");

            migrationBuilder.CreateIndex(
                name: "ix_outreach_batches_referrer_contact_id",
                table: "outreach_batches",
                column: "referrer_contact_id");

            migrationBuilder.CreateIndex(
                name: "ix_outreach_batches_tenant_id_created_at",
                table: "outreach_batches",
                columns: new[] { "tenant_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_outreach_targets_assigned_user_id",
                table: "outreach_targets",
                column: "assigned_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_outreach_targets_batch_id",
                table: "outreach_targets",
                column: "batch_id");

            migrationBuilder.CreateIndex(
                name: "ix_outreach_targets_connection_id_zalo_uid",
                table: "outreach_targets",
                columns: new[] { "connection_id", "zalo_uid" });

            migrationBuilder.CreateIndex(
                name: "ix_outreach_targets_contact_id",
                table: "outreach_targets",
                column: "contact_id",
                unique: true,
                filter: "state IN ('queued','invited','friend','messaged')");

            migrationBuilder.CreateIndex(
                name: "ix_outreach_targets_tenant_id_state_next_action_at",
                table: "outreach_targets",
                columns: new[] { "tenant_id", "state", "next_action_at" });

            migrationBuilder.AddForeignKey(
                name: "fk_contacts_contacts_referred_by_contact_id",
                table: "contacts",
                column: "referred_by_contact_id",
                principalTable: "contacts",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_contacts_contacts_referred_by_contact_id",
                table: "contacts");

            migrationBuilder.DropTable(
                name: "outreach_targets");

            migrationBuilder.DropTable(
                name: "outreach_batches");

            migrationBuilder.DropIndex(
                name: "ix_contacts_referred_by_contact_id",
                table: "contacts");

            migrationBuilder.DropColumn(
                name: "origin",
                table: "contacts");

            migrationBuilder.DropColumn(
                name: "referred_by_contact_id",
                table: "contacts");
        }
    }
}
