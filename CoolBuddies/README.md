# CoolBuddies

Website for an air conditioning and refrigeration business, with online ordering and PayFast payments.

## Structure
- `frontend/`: the website (`index.html`, `css/styles.css`, `js/app.js`, `images/`)
- `api/CoolBuddies.Api/`: ASP.NET Core (.NET 8) API using Dapper and SQL Server
- `database/database.sql`: SQL Server schema, seed data and a limited API login

## Run locally
1. Run `database/database.sql` in SQL Server, after changing the login password inside it.
2. In `api/CoolBuddies.Api`, store your settings as user secrets (never commit them):
   ```
   dotnet user-secrets init
   dotnet user-secrets set "ConnectionStrings:Default" "Server=...;Database=CoolBuddies;User Id=coolbuddies_api;Password=...;TrustServerCertificate=True"
   dotnet user-secrets set "PayFast:MerchantId" "..."
   dotnet user-secrets set "PayFast:MerchantKey" "..."
   dotnet user-secrets set "PayFast:Passphrase" "..."
   ```
3. Set `PublicSiteUrl` (where the frontend runs) and `PublicApiUrl` (public https address of the API) in `appsettings.json`, then `dotnet run`.
4. Set `CONFIG.API` at the top of `frontend/js/app.js` to the API address, and serve `frontend/` (for example with VS Code Live Server on port 5500).

PayFast sends payment confirmations to `PublicApiUrl`, so for end-to-end tests the API must be reachable from the internet (deploy it, or use a tunnel). Keep `PayFast:Sandbox` true until testing is complete.

## Admin orders page
1. In `api/CoolBuddies.Api`, run `dotnet run -- hash-password "your long password"`. It prints a hash.
2. Store it: `dotnet user-secrets set "Admin:PasswordHash" "<the hash>"` (on a host, set `Admin__PasswordHash`).
3. Set `API` at the top of the script in `frontend/admin.html`, serve `frontend/`, open `admin.html` and sign in.
Sign-in is limited to 5 attempts a minute, and the session ends after 2 hours.

## Security notes
- Never commit secrets: connection strings, PayFast keys or passphrase, database passwords.
- Prices are always read from the database, and orders are only marked paid after PayFast's signature, server confirmation and amount checks pass.
