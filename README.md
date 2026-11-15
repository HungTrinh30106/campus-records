# Campus Records

A full-stack student records management application built with **React, TypeScript, Tailwind CSS, ASP.NET Core and MongoDB Atlas**.

The application combines everyday academic administration with server-side access control, private document storage, audit trails and encrypted backups. The interface is in Vietnamese.

## Features

- **Student records:** create and update profiles, manage departments, search, filter, sort and paginate records.
- **Role-based access:** Admin, Staff and Student roles with department and individual record scopes enforced by the API.
- **Sensitive data protection:** mask contact and identification fields and omit GPA for Staff responses and exports.
- **Private documents:** upload PDF, PNG or JPEG files, validate size and file signatures, and authorize downloads against the associated record.
- **CSV reports:** apply the same scope and masking as the API, limit exports and neutralize spreadsheet formula prefixes.
- **Account management:** ASP.NET Core Identity password hashing, lockout, password changes and session revocation after account or permission changes.
- **Operations dashboard:** request metrics, latency, process CPU/RAM, persistent security alerts and MongoDB change events.
- **Encrypted backups:** shared API/CLI archive implementation, manual and scheduled jobs, verification and restoration into a new database and upload directory.

## Tech stack

| Layer | Technology |
| --- | --- |
| Frontend | React 19, TypeScript, Vite, Tailwind CSS 4, React Router |
| Backend | C#, ASP.NET Core / .NET 10, ASP.NET Core Identity |
| Database | MongoDB Atlas, MongoDB C# Driver |
| Testing | xUnit, ASP.NET Core integration testing, Vitest, React Testing Library |
| Backup encryption | AES-256-GCM and PBKDF2-SHA256 |

## Architecture

```text
React application
    |
    | Same-origin /api requests, authentication cookie and CSRF token
    v
ASP.NET Core API
    | Identity, validation, role checks, record scope and response DTOs
    |
    +-- MongoDB: users, departments, students, document metadata and journals
    +-- Private filesystem: uploaded document contents
    +-- Backup Core: encrypted BSON and document archives
```

The browser never connects directly to MongoDB. Database credentials and backup keys remain on the backend. MongoDB stores document metadata; uploaded file contents are stored outside the public web directory.

## Access model

| Role | Data scope | Capabilities |
| --- | --- | --- |
| Admin | Entire application | Manage records, departments, account permissions, reports, audit and operations |
| Staff | Assigned department | Create records and update permitted academic fields; view masked data and scoped reports |
| Student | Linked student record | View their own record and update permitted contact fields |

Authorization is enforced on the server for both record and document requests. Changing an ID in a URL does not remove the caller's scope.

## Run locally

### Prerequisites

- .NET 10 SDK
- Node.js 22.12 or newer supported LTS version
- PowerShell 7 for the supplied scripts
- MongoDB Atlas or a local MongoDB instance

```powershell
git clone https://github.com/HungTrinh30106/campus-records.git
cd campus-records
.\Setup.cmd
```

Setup installs dependencies and prompts for the MongoDB connection string and a strong password for optional development seed accounts. It saves configuration in .NET User Secrets, outside the repository.

For Atlas, create a database user limited to the application's database and allow the API machine's IP address. Keep TLS certificate verification enabled.

Start the API in the first terminal:

```powershell
# First run only, when development seed data is wanted:
.\Start-Api.cmd -Seed

# Subsequent runs:
.\Start-Api.cmd
```

Start the frontend in a second terminal:

```powershell
.\Start-Frontend.cmd
```

Open **http://127.0.0.1:5173**. API health is available at **http://localhost:5080/api/health**. Keep both processes running.

With seeding enabled in Development, the initial accounts are `admin@campus.local`, `staff@campus.local` and `student@campus.local`. Their password is the value supplied during Setup; there is no password embedded in the repository. Running seed again does not reset existing passwords.

You can also launch the processes directly after configuring secrets:

```powershell
dotnet run --project backend/StudentRecords.Api
npm --prefix frontend run dev
```

### Configuration

| Key | Purpose |
| --- | --- |
| `Mongo:ConnectionString` | Private MongoDB connection string |
| `Mongo:Database` | Application database, default `campus_records` |
| `Storage:Path` | Private upload directory |
| `ChangeStreams:Enabled` | Opt-in write-event tracking on Atlas or a replica set |
| `Backup:Path` | Private archive directory, separate from uploads |
| `Backup:Password` | Archive encryption secret, stored separately from archives |
| `Backup:Enabled` | Enable the background schedule while the API is running |

Environment variables use double underscores, such as `Mongo__ConnectionString`. User Secrets use the ID `CampusRecords.LocalDevelopment`. User Secrets are for development and are not an encrypted production vault.

On Windows, `Setup-Operations.cmd` configures change streams and daily encrypted backups, retains seven owned archives and preserves an existing backup key. Restart the API after configuration.

## Build and test

```powershell
pwsh -NoProfile -File scripts/Build.ps1
pwsh -NoProfile -File scripts/Test.ps1
```

The build script publishes the API and the compiled frontend into an ignored `artifacts/` directory. The test script starts a disposable local MongoDB process, runs the .NET test suites and frontend tests, builds the frontend, then cleans up its database directory. Install MongoDB Community with `mongod` available before running the complete integration suite. Change-stream tests start an isolated replica set.

For frontend-only checks:

```powershell
npm --prefix frontend test
npm --prefix frontend run build
```

Tests cover role and record scope, IDOR, CSRF, lockout, session revocation, file validation, report masking, backup integrity, BSON/file restoration, retention ownership and change-stream recovery. Additional isolated security labs are available through `SecurityLab.cmd` and `XssLab.cmd`.

Never use a production database as a test target.

## Project structure

```text
backend/StudentRecords.Api/       API, Identity store and authorization
frontend/                        React application and component tests
shared/StudentRecords.Backup.Core/ Shared archive, encryption and restore logic
tests/                           API and backup integration tests
tools/StudentRecords.Backup/      Backup, verify and restore CLI
tools/StudentRecords.SecurityLab/ Isolated native MongoDB checks
tools/xss-lab/                    Standalone localhost security example
scripts/                         Setup, development, build and test tooling
```

## Operational boundaries

- Change streams track writes and minimal event metadata. They do not audit reads or identify users performing direct MongoDB writes.
- Coordinated backup admission covers one API instance. Stop other writers to keep database records and uploaded files consistent. The scheduler runs only while the API is running.
- Restore into a new, empty database and upload directory, then validate before directing the application to them. Preserve the backup key separately.
- Rate limiting and caching are per process. Production needs HTTPS, appropriate secret storage, filesystem permissions and deployment-specific monitoring.
- File type checks do not replace malware scanning. MFA, email-based password recovery and multi-instance high availability are outside the current implementation.
