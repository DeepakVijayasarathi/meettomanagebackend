using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace iucs.meettomanage.domain.Migrations.Master
{
    /// <inheritdoc />
    public partial class AddSaasBilling : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Gap-free-enough, concurrency-safe numbering for platform_invoice.invoice_number.
            migrationBuilder.Sql("CREATE SEQUENCE IF NOT EXISTS platform_invoice_number_seq START WITH 1 INCREMENT BY 1;");

            migrationBuilder.CreateTable(
                name: "plan",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    name = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                    description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    is_public = table.Column<bool>(type: "boolean", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    trial_days = table.Column<int>(type: "integer", nullable: false),
                    sort_order = table.Column<int>(type: "integer", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_plan", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "platform_email_template",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    subject = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    body = table.Column<string>(type: "text", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_platform_email_template", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "platform_invoice",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    invoice_number = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    subscription_id = table.Column<Guid>(type: "uuid", nullable: true),
                    period_start_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    period_end_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    subtotal = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    tax_rate_percent = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: false),
                    tax_amount = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    total = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    issued_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    due_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    paid_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    target_plan_price_id = table.Column<Guid>(type: "uuid", nullable: true),
                    gateway_order_ref = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    payment_url = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_platform_invoice", x => x.id);
                    table.ForeignKey(
                        name: "fk_platform_invoice_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "platform_webhook_event",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    external_event_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    event_type = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    payload = table.Column<string>(type: "text", nullable: false),
                    received_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    processed_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_platform_webhook_event", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "subscription_reminder_log",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    subscription_id = table.Column<Guid>(type: "uuid", nullable: false),
                    rule_key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    cycle_key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    channel = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    recipient = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    error = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    sent_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_subscription_reminder_log", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "tenant_billing_profile",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    legal_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    billing_email = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    billing_phone = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    gstin = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    address = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    state = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    country = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: false),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tenant_billing_profile", x => x.tenant_id);
                    table.ForeignKey(
                        name: "fk_tenant_billing_profile_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "tenant_entitlement_override",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    value_type = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    value = table.Column<long>(type: "bigint", nullable: false),
                    expires_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tenant_entitlement_override", x => x.id);
                    table.ForeignKey(
                        name: "fk_tenant_entitlement_override_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "tenant_subscription_event",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    subscription_id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    detail = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    actor = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tenant_subscription_event", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "tenant_usage_snapshot",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    snapshot_date = table.Column<DateOnly>(type: "date", nullable: false),
                    active_students = table.Column<int>(type: "integer", nullable: false),
                    staff_count = table.Column<int>(type: "integer", nullable: false),
                    storage_bytes = table.Column<long>(type: "bigint", nullable: false),
                    session_minutes_month = table.Column<int>(type: "integer", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tenant_usage_snapshot", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "plan_entitlement",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    plan_id = table.Column<Guid>(type: "uuid", nullable: false),
                    key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    value_type = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    value = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_plan_entitlement", x => x.id);
                    table.ForeignKey(
                        name: "fk_plan_entitlement_plan_plan_id",
                        column: x => x.plan_id,
                        principalTable: "plan",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "plan_price",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    plan_id = table.Column<Guid>(type: "uuid", nullable: false),
                    billing_interval = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    amount = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    gateway_plan_ref = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    effective_from_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_plan_price", x => x.id);
                    table.ForeignKey(
                        name: "fk_plan_price_plan_plan_id",
                        column: x => x.plan_id,
                        principalTable: "plan",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "platform_invoice_line",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    invoice_id = table.Column<Guid>(type: "uuid", nullable: false),
                    description = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    quantity = table.Column<int>(type: "integer", nullable: false),
                    unit_amount = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    amount = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_platform_invoice_line", x => x.id);
                    table.ForeignKey(
                        name: "fk_platform_invoice_line_platform_invoice_invoice_id",
                        column: x => x.invoice_id,
                        principalTable: "platform_invoice",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "platform_payment",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    invoice_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    gateway = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    gateway_payment_ref = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    amount = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    failure_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_platform_payment", x => x.id);
                    table.ForeignKey(
                        name: "fk_platform_payment_platform_invoice_invoice_id",
                        column: x => x.invoice_id,
                        principalTable: "platform_invoice",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "tenant_subscription",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    plan_id = table.Column<Guid>(type: "uuid", nullable: false),
                    plan_price_id = table.Column<Guid>(type: "uuid", nullable: true),
                    status = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    trial_ends_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    current_period_start_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    current_period_end_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    cancel_at_period_end = table.Column<bool>(type: "boolean", nullable: false),
                    cancelled_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    grace_ends_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    suspended_by_billing = table.Column<bool>(type: "boolean", nullable: false),
                    pending_plan_price_id = table.Column<Guid>(type: "uuid", nullable: true),
                    gateway_customer_ref = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    gateway_subscription_ref = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tenant_subscription", x => x.id);
                    table.ForeignKey(
                        name: "fk_tenant_subscription_plan_plan_id",
                        column: x => x.plan_id,
                        principalTable: "plan",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_tenant_subscription_plan_price_plan_price_id",
                        column: x => x.plan_price_id,
                        principalTable: "plan_price",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_tenant_subscription_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ux_plan_code",
                table: "plan",
                column: "code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_plan_entitlement_plan_id_key",
                table: "plan_entitlement",
                columns: new[] { "plan_id", "key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_plan_price_plan_id_billing_interval_is_active",
                table: "plan_price",
                columns: new[] { "plan_id", "billing_interval", "is_active" });

            migrationBuilder.CreateIndex(
                name: "ux_platform_email_template_key",
                table: "platform_email_template",
                column: "key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_platform_invoice_gateway_order_ref",
                table: "platform_invoice",
                column: "gateway_order_ref");

            migrationBuilder.CreateIndex(
                name: "ix_platform_invoice_status_due_at_utc",
                table: "platform_invoice",
                columns: new[] { "status", "due_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_platform_invoice_tenant_id_issued_at_utc",
                table: "platform_invoice",
                columns: new[] { "tenant_id", "issued_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ux_platform_invoice_invoice_number",
                table: "platform_invoice",
                column: "invoice_number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_platform_invoice_line_invoice_id",
                table: "platform_invoice_line",
                column: "invoice_id");

            migrationBuilder.CreateIndex(
                name: "ix_platform_payment_gateway_gateway_payment_ref",
                table: "platform_payment",
                columns: new[] { "gateway", "gateway_payment_ref" });

            migrationBuilder.CreateIndex(
                name: "ix_platform_payment_invoice_id",
                table: "platform_payment",
                column: "invoice_id");

            migrationBuilder.CreateIndex(
                name: "ix_platform_payment_tenant_id",
                table: "platform_payment",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ux_platform_webhook_event_provider_external_event_id",
                table: "platform_webhook_event",
                columns: new[] { "provider", "external_event_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_subscription_reminder_log_tenant_id_created_at_utc",
                table: "subscription_reminder_log",
                columns: new[] { "tenant_id", "created_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ux_subscription_reminder_log_subscription_id_rule_key_cycle_key_channel",
                table: "subscription_reminder_log",
                columns: new[] { "subscription_id", "rule_key", "cycle_key", "channel" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_tenant_entitlement_override_tenant_id_key",
                table: "tenant_entitlement_override",
                columns: new[] { "tenant_id", "key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_tenant_subscription_plan_id",
                table: "tenant_subscription",
                column: "plan_id");

            migrationBuilder.CreateIndex(
                name: "ix_tenant_subscription_plan_price_id",
                table: "tenant_subscription",
                column: "plan_price_id");

            migrationBuilder.CreateIndex(
                name: "ix_tenant_subscription_status_current_period_end_utc",
                table: "tenant_subscription",
                columns: new[] { "status", "current_period_end_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_tenant_subscription_trial_ends_at_utc",
                table: "tenant_subscription",
                column: "trial_ends_at_utc");

            migrationBuilder.CreateIndex(
                name: "ux_tenant_subscription_tenant_id",
                table: "tenant_subscription",
                column: "tenant_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_tenant_subscription_event_tenant_id_created_at_utc",
                table: "tenant_subscription_event",
                columns: new[] { "tenant_id", "created_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ux_tenant_usage_snapshot_tenant_id_snapshot_date",
                table: "tenant_usage_snapshot",
                columns: new[] { "tenant_id", "snapshot_date" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP SEQUENCE IF EXISTS platform_invoice_number_seq;");

            migrationBuilder.DropTable(
                name: "plan_entitlement");

            migrationBuilder.DropTable(
                name: "platform_email_template");

            migrationBuilder.DropTable(
                name: "platform_invoice_line");

            migrationBuilder.DropTable(
                name: "platform_payment");

            migrationBuilder.DropTable(
                name: "platform_webhook_event");

            migrationBuilder.DropTable(
                name: "subscription_reminder_log");

            migrationBuilder.DropTable(
                name: "tenant_billing_profile");

            migrationBuilder.DropTable(
                name: "tenant_entitlement_override");

            migrationBuilder.DropTable(
                name: "tenant_subscription");

            migrationBuilder.DropTable(
                name: "tenant_subscription_event");

            migrationBuilder.DropTable(
                name: "tenant_usage_snapshot");

            migrationBuilder.DropTable(
                name: "platform_invoice");

            migrationBuilder.DropTable(
                name: "plan_price");

            migrationBuilder.DropTable(
                name: "plan");
        }
    }
}
