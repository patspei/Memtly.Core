# Dependency security preparation — 2026-10-24

- MailKit **4.18.0 → 4.18.2** brings MimeKit 4.18.2 and
  System.Security.Cryptography.Pkcs **10.0.13**, fixing
  [GHSA-846f-f764-76mx](https://github.com/advisories/GHSA-846f-f764-76mx) and
  [GHSA-2fmv-rv2c-59x7](https://github.com/advisories/GHSA-2fmv-rv2c-59x7).
  [MailKit dependencies](https://www.nuget.org/packages/MailKit/4.18.2) and
  [MimeKit dependencies](https://www.nuget.org/packages/MimeKit/4.18.2).
- Microsoft.Data.Sqlite and the aligned EF/Identity 9.0.18 references move to
  **9.0.19**, the first subsequent patch using SQLitePCLRaw **2.1.12**. No EF
  major migration or transitive override is needed.
  [Microsoft.Data.Sqlite 9.0.19](https://www.nuget.org/packages/Microsoft.Data.Sqlite/9.0.19),
  [EF SQLite 9.0.19](https://www.nuget.org/packages/Microsoft.EntityFrameworkCore.Sqlite/9.0.19),
  [GHSA-2m69-gcr7-jv3q](https://github.com/advisories/GHSA-2m69-gcr7-jv3q).
  The advisory lists affected native package versions through 2.1.11; its
  “Patched versions: None” field predates the available 2.1.12 maintenance package.
- All six Core `packages.lock.json` files were regenerated with
  `dotnet restore --force-evaluate`; the consuming Community lockfile must be
  updated with the submodule pointer. Locked restore succeeds on Windows.
- Four existing npm advisories were resolved solely through compatible updates
  to `Memtly.Core/package-lock.json`: brace-expansion 5.0.9 → 5.0.12,
  fast-uri 3.1.8 → 3.1.9, serialize-javascript 7.0.6 → 7.1.4,
  source-map-js 1.2.1 → 1.2.2. No direct dependency was added or removed.

Verification commands (from the Core repository):

```sh
dotnet restore Memtly.Core.sln --locked-mode
dotnet package list --project Memtly.Core.sln --vulnerable --include-transitive --no-restore
cd Memtly.Core
npm ci
npm audit
```

The complete consuming solution audit reports no known vulnerable NuGet packages.
The targeted npm lock update reports zero npm vulnerabilities. Auditing remains
enabled; no security warnings are suppressed. This is an audit snapshot, not a
guarantee against future advisories.
