# Deploying the API to a VPS

The short version: publish it, give it a connection string, point nginx at it.
The database creates itself.

---

## 1. What you must set

Four settings. Everything else has a working default.

| Setting | Why |
|---|---|
| `Storefront:Database:ConnectionString` | Without it the API runs on the JSON files and every order is written to a file the next deploy overwrites. |
| `Storefront:Database:ServerVersion` | Without it the API asks MySQL its version at startup, so it cannot start while the database is still coming up. |
| `Storefront:Admin:Passcode` | Blank means every admin endpoint answers **503**. The admin panel will not work until this is set. |
| `Storefront:AllowedOrigins` | Defaults to `localhost`. A browser will block the real storefront's requests until its own URL is listed. |

Set them as environment variables — `__` is the separator for nesting:

```bash
Storefront__Database__ConnectionString="Server=localhost;Port=3306;Database=gopicrackers;User ID=gopi;Password=…;"
Storefront__Database__ServerVersion="8.0.36-mysql"     # or "10.11.6-mariadb"
Storefront__Admin__Passcode="…"
Storefront__AllowedOrigins__0="https://admin.skvpyros.in"
# plus the storefront's own URL, and the localhost entries you still want
```

`ConnectionStrings__GopiCrackers` works too, if your host only knows how to set
connection strings the standard way.

> Run `SELECT VERSION();` on the server to get the value for `ServerVersion`.
> It is `<version>-mysql` or `<version>-mariadb` — the suffix matters.

---

## 2. The database creates itself

You do **not** need to run `CREATE DATABASE`, and you do not need to run
migrations. On startup the API, in this order:

1. **Creates the database** named in the connection string, if the server does
   not have it yet — `utf8mb4` / `utf8mb4_unicode_ci`.
2. **Applies the migrations**, creating all 15 tables.
3. **Seeds the catalogue** from the JSON files it ships with — the full 2026
   price list, categories, combos, offers, banners and FAQs.

Step 3 only ever fills tables that are **empty**. A restart cannot overwrite a
morning's edits, and it will never re-import a stale file over live orders.

### The MySQL user needs

```sql
CREATE USER 'gopi'@'localhost' IDENTIFIED BY '…';
GRANT ALL PRIVILEGES ON `gopicrackers`.* TO 'gopi'@'localhost';

-- Only needed so the API can create the database itself on the first run.
-- Skip it and create the database by hand instead:
--   CREATE DATABASE `gopicrackers` CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;
GRANT CREATE ON *.* TO 'gopi'@'localhost';

FLUSH PRIVILEGES;
```

If the user cannot create databases, startup says so and tells you the exact
`CREATE DATABASE` to run. It does not fail silently, and it does not quietly
fall back to the JSON files.

---

## 3. Publish and run

```bash
dotnet publish api/GopiCrackers.Api/GopiCrackers.Api.csproj -c Release -o /var/www/gopicrackers-api
```

`systemd` unit — `/etc/systemd/system/gopicrackers-api.service`:

```ini
[Unit]
Description=Gopi Crackers API
After=network.target mysql.service

[Service]
WorkingDirectory=/var/www/gopicrackers-api
ExecStart=/usr/bin/dotnet /var/www/gopicrackers-api/GopiCrackers.Api.dll
Restart=always
RestartSec=10
User=www-data

Environment=ASPNETCORE_ENVIRONMENT=Production
Environment=ASPNETCORE_URLS=http://127.0.0.1:5000
Environment=Storefront__Database__ConnectionString=Server=localhost;Port=3306;Database=gopicrackers;User ID=gopi;Password=…;
Environment=Storefront__Database__ServerVersion=8.0.36-mysql
Environment=Storefront__Admin__Passcode=…
# AllowedOrigins is set in appsettings.json instead — an array reads badly
# as environment variables, and none of it is secret.

[Install]
WantedBy=multi-user.target
```

Bind Kestrel to `127.0.0.1`, never `0.0.0.0` — nginx is what should be reachable.

```bash
sudo systemctl enable --now gopicrackers-api
sudo journalctl -u gopicrackers-api -f
```

---

## 4. nginx

```nginx
location /api/ {
    proxy_pass         http://127.0.0.1:5000;
    proxy_http_version 1.1;

    proxy_set_header Host              $host;
    proxy_set_header X-Real-IP         $remote_addr;
    proxy_set_header X-Forwarded-For   $proxy_add_x_forwarded_for;
    proxy_set_header X-Forwarded-Proto $scheme;   # required
}
```

`X-Forwarded-Proto` is not optional. Without it the API believes every request
arrived over plain HTTP; if an HTTPS port is ever configured it will redirect to
`https://`, nginx will forward that back as HTTP, and **every endpoint becomes a
redirect loop**. The API honours the header because
`Storefront:Hosting:BehindReverseProxy` defaults to `true` — leave it on unless
Kestrel itself faces the internet.

---

## 5. Check it worked

```bash
curl -s https://api.skvpyros.in/api/health | jq
```

`storage.backend` must say `mysql`. If it says `files`, the connection string
did not reach the process.

Then, with your passcode:

```bash
curl -s -H "X-Admin-Passcode: …" https://api.skvpyros.in/api/admin/database | jq
```

You want `canConnect: true`, `pendingMigrations: []`, `error: null`, and
`tables` showing non-zero `products` and `categories`. The password is redacted
in that response, so it is safe to paste when asking for help.

---

## 6. Verifying against your real server, before you trust it

The test suite has a MySQL-backed half that is skipped unless you point it at a
server. Once you have the connection string:

```bash
GOPI_TEST_MYSQL="Server=…;Port=3306;Database=gopicrackers;User ID=…;Password=…;" \
  dotnet test api/GopiCrackers.Api.Tests
```

It does **not** touch the database you name. It works in a scratch database
beside it — `gopicrackers_apitest` — which it leaves the API to create for
itself, so the run proves the thing worth proving: that pointing this API at a
server with no schema is enough. It then checks the schema, the seed, every
store's backend, all 60 GET endpoints and the admin lock, on MySQL.

Add `GOPI_TEST_MYSQL_DROP=1` to have the scratch database dropped afterwards
instead of left for inspection.

Run it from a machine that can reach the database — if MySQL only listens on
the VPS's localhost, run it there, or open an SSH tunnel.

---

## 7. The two front ends

> The storefront and the admin live in the **Fire-Crackers** repo, not this
> one. The files named below are there. This section is here because the two
> halves have to agree about the URL, and that agreement is a deployment
> concern rather than a front-end one.

Both apps read one variable, `VITE_API_URL`, and it must include the `/api`
suffix — the clients ask for paths like `/bootstrap` and `/admin/summary`, and
that is what they hang off.

It is already set for production builds, in `.env.production` at the repo root
(storefront) and in `admin/.env.production`:

```
VITE_API_URL=https://api.skvpyros.in/api
```

Vite inlines this at **build time**, so changing it means rebuilding — there is
no runtime config to edit on the server:

```bash
npm run build          # storefront -> dist/
npm run build:admin    # admin      -> admin/dist/
```

Leave it unset and the clients fall back to a same-origin `/api`, which is what
the dev server proxies.

### Three things that must line up

**1. The API must answer over HTTPS.** A page served over HTTPS may not fetch
over plain HTTP — the browser blocks it as mixed content before the request is
sent, so an `http://` → `https://` redirect does not help. Every request fails
and the shop shows its offline fallback.

**2. Both origins must be in `Storefront:AllowedOrigins`.** CORS is enforced in
the browser, not in the API: an origin that is not listed still receives a
normal 200, and the browser discards it before the app sees it. Nothing appears
in the API's logs. `https://admin.skvpyros.in` is listed; **the storefront's own
URL still needs adding** once its domain is decided.

Setting that key in `appsettings.json` *replaces* the built-in list rather than
adding to it, so the eight localhost entries have to stay or local development
stops working against the API. `CorsTests` guards both halves of that.

**3. The admin needs a passcode.** `Storefront:Admin:Passcode` blank means every
admin endpoint answers 503 and the gate cannot let anyone in.

### Working locally against the live API

`npm run dev` proxies `/api` to `http://localhost:5080` by default. To point a
dev server at the deployed API instead — no CORS involved, because the proxy
makes it same-origin:

```bash
VITE_API_TARGET=https://api.skvpyros.in npm run dev
```

### If the shop and the API ever share a domain

Serving the API under the shop's own origin — an nginx rule sending `/api` to
it — is the simpler setup: drop `VITE_API_URL`, and the same-origin default
applies. No CORS, no preflight, and mixed content becomes impossible.

---

## Troubleshooting

**"Could not reach the MySQL server to create '…'"** — the server did not
answer at all, so this is not a missing database. Check host, port, credentials,
and that the user may connect from this machine.

**"could not create the database … most likely the user has no CREATE
privilege"** — grant `CREATE`, or create the database by hand with the statement
in the message.

**"Could not reach MySQL to find out what version it is"** — `ServerVersion` is
blank and the server was not up when the API started. Set it and startup stops
depending on the database being awake.

**Admin endpoints return 503** — `Storefront:Admin:Passcode` is blank.

**The storefront loads but fetches nothing** — its URL is not in
`Storefront:AllowedOrigins`. The browser console will say so.

**Every request redirects forever** — nginx is not sending
`X-Forwarded-Proto`, or `BehindReverseProxy` was turned off.

**The shop loads but every panel is empty, and the API's logs look fine** —
this is CORS. The requests are arriving and being answered; the browser is
discarding the responses. The console will name the origin it wanted allowed.
Add that exact string — scheme, host and port all have to match.

**The console says "Mixed Content" or "blocked: mixed-content"** — the page is
HTTPS and `VITE_API_URL` is `http://`. Fix TLS on the API; a redirect will not
do.

**Requests go to `https://api.skvpyros.in/products` and 404** — `VITE_API_URL`
is missing its `/api` suffix.

**A rebuilt front end still calls the old URL** — Vite inlines `VITE_API_URL`
at build time. Rebuild, and make sure the host is serving the new `dist/`.
