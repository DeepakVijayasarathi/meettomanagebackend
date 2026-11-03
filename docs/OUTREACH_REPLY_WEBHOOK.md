# Outreach reply webhook

The marketing reply monitor accepts normalized inbound email events at:

```text
POST https://<api-host>/api/marketing/outreach/inbound/reply
```

## Production setup

1. Generate a random secret with at least 32 bytes.
2. In Jenkins, create a **Secret text** credential named
   `meettomanage-outreach-webhook-secret`.
3. Redeploy the backend. The pipeline injects it as
   `Outreach__InboundWebhookSecret`.
4. In Zoho Flow, Make, Zapier, or the inbound-mail provider, send a POST request
   with this header:

```text
X-Outreach-Webhook-Secret: <the Jenkins secret>
Content-Type: application/json
```

Use this JSON shape:

```json
{
  "from": "owner@example.com",
  "subject": "Re: Quick question",
  "text": "Yes, please send pricing and demo times.",
  "externalId": "provider-message-id"
}
```

Map `from` to the sender's plain email address, `text` to the plain-text body,
and `externalId` to the provider's immutable message ID. Duplicate message IDs
are ignored safely. The sender must already exist as an outreach prospect.

## Expected responses

- `{ "matched": true, "prospectId": "..." }`: synchronized successfully.
- `{ "matched": true, "duplicate": true }`: provider retried an event already handled.
- `{ "matched": false }`: sender is not an outreach prospect.
- `401 Unauthorized`: missing/incorrect secret or the server secret is not configured.

On a successful match, scheduled follow-ups stop immediately, the reply appears
in the AI monitor, the prospect is classified, and a sales task is created when
human action is appropriate.

## Built-in Zoho IMAP monitor (no Zoho Flow required)

Create one Jenkins **Username with password** credential with ID
`meettomanage-zoho-imap`. Set the username to the full Zoho mailbox address and
the password to a Zoho application-specific password. The deployed worker connects
to `imap.zoho.in:993` over TLS every 60 seconds. It reads unread Inbox messages,
passes them through the same secure webhook processing, and marks a message read
only after the API accepts it. Failed messages remain unread and retry later.

The mailbox should be dedicated to outreach replies. Otherwise unrelated unread
mail will also be inspected (unknown senders safely return `matched: false`).

## Safe verification

After deployment, send one test event for an existing test prospect. Confirm it
appears under **Marketing Console → Replies**, then retry the same `externalId`
and confirm the response reports `duplicate: true`.
