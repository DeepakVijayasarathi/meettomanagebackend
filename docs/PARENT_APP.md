# Mobile parent app: one API address, everything else from the admin side

The parent app (`meet-to-manage-parent-app`, Expo / React Native) is built with **a single setting**:

```
EXPO_PUBLIC_API_BASE_URL=https://api.example.com
```

Everything else is configured by the academy's administrator in **Tenant Admin -> Tenant configuration** and read by
the app from the API after a family signs in. Changing it needs no app release.

## How a family reaches their academy

1. The family signs in at `POST /api/auth/login` on the one API address with their email or mobile number and PIN.
   The API finds their academy from the login directory (no tenant code or `/t/<code>` path in the app).
2. The token carries the academy, so every later call is a plain `/api/...` path.
3. The app then reads `GET /api/app/config` (signed in) for the academy's settings.

## Application access (per tenant)

Each tenant has an **app access granted** flag (`tenants.app_access_granted`).

* It is chosen when the tenant is created (the *Grant mobile app access* checkbox in **New client**, or
  `appAccessGranted` in `POST /api/platform/tenants`; omitted means granted). Tenants that existed before this
  feature were all set to granted.
* Change it later from the client's menu, from **Tenant configuration -> Parent app**, or with
  `PUT /api/platform/tenants/{id}/app-access` `{ "granted": true|false }`. It takes effect on the next request.
* The app marks every request with `X-Client: parent-app`. When the flag is off, those requests, **including
  sign-in**, are refused with `403` and a plain message. The website is not affected.
* The app signs a family out and shows the message when the academy's access is withdrawn.

> This is an entitlement, not a security boundary: the header is sent by the app and could be omitted by a determined
> user, who would then simply be using the API like the website does. The user's token still authenticates them.

## What the administrator controls (read from `GET /api/app/config`)

| Value | Where it is set | Used by the app for |
|---|---|---|
| Web portal address | Parent app tab (`app.webBaseUrl`); empty = server's `Frontend:BaseUrl` | Opening payments, adding a child and other website pages |
| Announcement | Parent app tab (`app.announcement`) | Banner at the top of the dashboard |
| Support email / phone | Parent app tab (`org.supportEmail`, `org.supportPhone`) | Support contact |
| Name, logo, colours, fonts | Branding tab (`brand.*`) | Delivered in the config for branding the app |

Only whitelisted keys are returned, and the web address is sent only if it is an absolute `http(s)` URL.

## Not done yet

* The app reads the brand (name, logo, colours, fonts) but does not restyle its own screens with it yet; it uses
  its built-in orange theme. The data is already available in `brand` of the config response.
* Before sign-in the app cannot know the academy (that is by design: the login screen would otherwise reveal which
  accounts exist), so the sign-in screen shows the app's default look.
