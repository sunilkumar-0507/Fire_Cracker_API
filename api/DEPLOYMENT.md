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
```

**Set `AllowedOrigins` in `appsettings.json`, not as an environment variable.**
An array element is addressed by index, so `Storefront__AllowedOrigins__0` does
not add an origin — it *overwrites* the first one, which in `appsettings.json`
is the storefront. Setting it to the admin URL is how you end up with a working
admin panel and a shop that cannot read a single response. The list is already
correct in that file and none of it is secret.

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
dotnet publish api/GopiCrackers.Api/GopiCrackers.Api.csproj -c Release -o /var/www/api
```

`systemd` unit — `/etc/systemd/system/gopicrackers-api.service`:

```ini
[Unit]
Description=Gopi Crackers API
After=network.target mysql.service

[Service]
WorkingDirectory=/var/www/api
ExecStart=/usr/bin/dotnet /var/www/api/GopiCrackers.Api.dll
Restart=always
RestartSec=10
User=www-data

Environment=ASPNETCORE_ENVIRONMENT=Production
Environment=ASPNETCORE_URLS=http://127.0.0.1:5000
Environment=Storefront__Database__ConnectionString=Server=localhost;Port=3306;Database=gopicrackers;User ID=gopi;Password=…;
Environment=Storefront__Database__ServerVersion=8.0.36-mysql
Environment=Storefront__Admin__Passcode=…
# AllowedOrigins is set in appsettings.json instead — see section 1. An index
# here overwrites an entry rather than adding one, and none of it is secret.

[Install]
WantedBy=multi-user.target
```

Bind Kestrel to `127.0.0.1`, never `0.0.0.0` — nginx is what should be reachable.
`http://127.0.0.1:5000` is also what the API binds when `ASPNETCORE_URLS` is
missing altogether, so a unit file that loses that line still cannot be reached
from off the machine. Confirm it either way in the log:

```bash
sudo journalctl -u gopicrackers-api | grep "Now listening on"
#   Now listening on: http://127.0.0.1:5000

sudo ss -lntp | grep :5000
#   LISTEN 0 512 127.0.0.1:5000   <- correct
#   LISTEN 0 512     *:5000       <- exposed; fix ASPNETCORE_URLS
```

```bash
sudo systemctl enable --now gopicrackers-api
sudo journalctl -u gopicrackers-api -f
```

---

## 4. nginx

`api.skvpyros.in` is its own server block, and everything under it goes to the
API — the root `/` is the endpoint index, so proxy `/` rather than only `/api/`.

```nginx
server {
    listen 443 ssl http2;
    listen [::]:443 ssl http2;
    server_name api.skvpyros.in;

    ssl_certificate     /etc/letsencrypt/live/api.skvpyros.in/fullchain.pem;
    ssl_certificate_key /etc/letsencrypt/live/api.skvpyros.in/privkey.pem;

    location / {
        proxy_pass         http://127.0.0.1:5000;
        proxy_http_version 1.1;

        proxy_set_header Host              $host;
        proxy_set_header X-Real-IP         $remote_addr;
        proxy_set_header X-Forwarded-For   $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto $scheme;   # required

        # Do NOT add add_header Access-Control-Allow-Origin here. See below.
    }
}

server {
    listen 80;
    listen [::]:80;
    server_name api.skvpyros.in;
    return 301 https://$host$request_uri;
}
```

**`X-Forwarded-Proto` is not optional.** Without it the API believes every
request arrived over plain HTTP, answers `307` to the `https://` address, and
nginx forwards that back as HTTP — **every endpoint becomes a redirect loop**.
The API honours the header because `Storefront:Hosting:BehindReverseProxy`
defaults to `true`; leave it on unless Kestrel itself faces the internet.

**Do not let nginx add CORS headers of its own.** The API already sends them,
and a browser rejects a response carrying two `Access-Control-Allow-Origin`
headers just as firmly as one carrying none — with a message about the header
appearing twice, which reads like the API is at fault. If a previous attempt
left `add_header Access-Control-Allow-Origin …` in any block for this host,
remove it. `curl -sI` in section 5 will show the duplicate.

`OPTIONS` must reach the API too. It is what a browser sends before every admin
request, and the API answers it. An nginx rule that short-circuits `OPTIONS`
with its own `204` takes that answer away.

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

### CORS, which the two checks above cannot tell you anything about

Neither `curl` above sends an `Origin`, and without one the API has no reason to
answer with a CORS header. That is exactly the trap: the API looks healthy from
the command line while every browser discards its responses. Send the origin the
browser would:

```bash
curl -sI -H 'Origin: https://skvpyros.in' https://api.skvpyros.in/api/health | grep -Ei 'HTTP/|access-control|vary'
```

Expected — `200`, the origin echoed back, and exactly **one** allow header:

```
HTTP/2 200
access-control-allow-origin: https://skvpyros.in
vary: Origin
```

Then the preflight, which is the one the admin panel lives or dies on. It is
what a browser sends by itself before every `X-Admin-Passcode` request, because
that is not a header it will send unasked:

```bash
curl -sI -X OPTIONS https://api.skvpyros.in/api/admin/summary -H 'Origin: https://admin.skvpyros.in' -H 'Access-Control-Request-Method: GET' -H 'Access-Control-Request-Headers: x-admin-passcode' | grep -Ei 'HTTP/|access-control'
```

Expected — `204`, and the passcode header named as permitted:

```
HTTP/2 204
access-control-allow-origin: https://admin.skvpyros.in
access-control-allow-headers: x-admin-passcode
access-control-allow-methods: GET
access-control-max-age: 3600
```

A `301` or `307` here means nginx is redirecting the `OPTIONS` before the API
sees it — a browser does not follow a redirect on a preflight, so that is the
admin panel unable to send a single request. Two `access-control-allow-origin`
lines means nginx is adding one of its own as well; remove it from the nginx
config.

Repeat the first check for `https://www.skvpyros.in`, and confirm that an origin
which should *not* work gets no header at all:

```bash
curl -sI -H 'Origin: https://example.com' https://api.skvpyros.in/api/health | grep -ci access-control-allow-origin
```

That one must print `0`. If it prints `1`, something in front of the API is
allowing every origin, and it is not this API.

### What the API says about it at startup

The running process lists the origins it loaded, which is the only place this is
visible from the server side:

```bash
sudo journalctl -u gopicrackers-api | grep "CORS:"
#   CORS: 11 origin(s) allowed: https://skvpyros.in, https://www.skvpyros.in, …
```

If a domain you expect is missing from that line, the process is not reading the
`appsettings.json` you edited — check you deployed it and restarted. A `warn`
naming an entry means it was not a usable origin and was dropped.

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
in the API's logs. `https://skvpyros.in`, `https://www.skvpyros.in` and
`https://admin.skvpyros.in` are all listed — but the running API only picks that
up when it is **redeployed and restarted**, because `appsettings.json` is read
at startup.

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
Check the startup log first — `journalctl -u gopicrackers-api | grep "CORS:"`
lists every origin the running process loaded. If the one you expect is not on
that line, the deployed `appsettings.json` is not the one you edited, or the
service was not restarted after it was.

**The shop is blocked but the admin works** — almost always
`Storefront__AllowedOrigins__0` set as an environment variable. Index 0 is the
storefront in `appsettings.json`, so setting that variable to the admin URL
replaces the shop rather than adding the admin. Unset it; the list in
`appsettings.json` already has all three domains.

**The console says the allow header "contains multiple values"** — nginx is
adding `Access-Control-Allow-Origin` on top of the one the API already sends.
Two of them is as fatal as none. Remove the `add_header` from the nginx config
for this host.

**The admin panel cannot send any request, and the shop is fine** — look at the
preflight rather than the request. `OPTIONS` is being redirected or answered by
nginx instead of reaching the API. The second `curl` in section 5 shows which:
you want `204` with `access-control-allow-headers: x-admin-passcode`, not a
`301` or `307`.

**One origin works and another does not, and both are in the list** — compare
them character for character against what the startup log printed. A trailing
slash, a path, capitals or a spelled-out `:443` are all tidied up now, but
`https://` vs `http://`, `www` vs the apex, and a differing port are genuinely
different origins and each needs its own entry.

**The console says "Mixed Content" or "blocked: mixed-content"** — the page is
HTTPS and `VITE_API_URL` is `http://`. Fix TLS on the API; a redirect will not
do.

**Requests go to `https://api.skvpyros.in/products` and 404** — `VITE_API_URL`
is missing its `/api` suffix.

**A rebuilt front end still calls the old URL** — Vite inlines `VITE_API_URL`
at build time. Rebuild, and make sure the host is serving the new `dist/`.
