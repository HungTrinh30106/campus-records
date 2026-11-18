# Campus Records

Campus Records is a student records portal for university administrators, department staff and students. An administrator can manage the university directory, a staff member can update records in one department, and a student can edit their own contact details. The interface is in Vietnamese.

**Frontend** · React 19, TypeScript, Tailwind CSS 4, Vite

**Backend** · C#, ASP.NET Core MVC on .NET 10, ASP.NET Core Identity

**Storage** · MongoDB Atlas for records and metadata, private filesystem storage for attachments

## Features and access

| Role | Record scope | What the user can do |
| --- | --- | --- |
| Admin | All departments | Manage student records, departments and account permissions. Review reports, audit events and backup runs. |
| Staff | Assigned department | Create records, update names and academic fields, search the directory and export a departmental report. Contact and ID fields are masked and GPA is withheld in reports. |
| Student | Own linked record | View their academic profile, edit email, phone and address, and manage supporting documents. |

- **Directory and reports.** Search, filter, sort and paginate student records. CSV exports use the caller's record scope and protect spreadsheet cells against formula interpretation.
- **Private attachments.** Upload PDF, PNG or JPEG files up to 5 MiB. The API checks the extension, declared content type and file signature, stores the file under a generated name and checks record access on download.
- **Accounts and audit.** Cookie sessions, CSRF tokens and Identity password hashing protect account actions. Repeated failed logins trigger lockout. Profile updates, document downloads and report exports leave audit entries.
- **Operations and recovery.** Admins can inspect request latency, resource usage and alerts. Optional MongoDB change streams track writes. Encrypted backups include both database records and attachments and can be restored into a fresh database and upload directory.

## How MVC works here

React renders the **View**. ASP.NET Core **Controllers** map HTTP routes to feature services. Services enforce the business rules and use the **Models** and MongoDB driver to read or update records. DTOs select the fields sent back to the browser.

For example, opening a student profile takes three steps.

1. The [React detail page](frontend/src/pages/StudentDetail.tsx) requests `GET /api/students/{id}`.
2. [StudentsController](backend/StudentRecords.Api/Controllers/StudentsController.cs) binds the route ID and calls [StudentsService](backend/StudentRecords.Api/Services/StudentsService.cs). The [record query](backend/StudentRecords.Api/Services/ApiSupport.cs) combines that ID with the signed-in user's scope before reading MongoDB.
3. The [student DTO](backend/StudentRecords.Api/Dtos/Dto.cs) applies the field visibility rules. React renders the resulting JSON. The same record can therefore show a full email address to an admin and a masked address to department staff.

### Repository layout

```text
backend/StudentRecords.Api/
  Program.cs         API entry point and startup
  Controllers/       HTTP routes grouped by feature
  Services/          Business rules, reports, audit and operations
  Models/            Student, department, user, document and audit records
  Dtos/              Response fields and masking
  Data/              MongoDB collections, indexes and development seed data
  Identity/          MongoDB user store for ASP.NET Core Identity
  Middleware/        Request validation, error handling and security checks
  Configurations/    Dependency injection and framework configuration
  Helpers/           Shared validation, storage and CSV helpers
frontend/src/
  pages/             Directory, profile, dashboard and administration screens
  components/        Forms, navigation and shared UI
  api.ts             Browser API client
shared/StudentRecords.Backup.Core/    Archive, encryption and restore logic
tools/StudentRecords.Backup/          Backup, verify and restore CLI
tests/                                API and backup test projects
scripts/                              Setup, development, build and test scripts
```

## Run locally on Windows

Install Git, the **.NET 10 SDK**, **Node.js 22.12+** with npm and **PowerShell 7**. The `.cmd` launchers can be called from Windows PowerShell 5.1 or a Visual Studio terminal and will locate the installed PowerShell 7 runtime.

### 1. Configure the database and install dependencies

Use a MongoDB Atlas database user with `readWrite` on `campus_records` and allow your machine's IP in the project's IP access list. Copy the driver's connection string for your cluster and supply the database user's credentials. For a local MongoDB server, use `mongodb://localhost:27017`.

```powershell
git clone https://github.com/HungTrinh30106/campus-records.git
cd campus-records
.\Setup.cmd
```

At the prompts, enter the connection string, then choose and confirm a password for the three local demo accounts. Use 10–128 characters with uppercase, lowercase, a digit and a special character. Setup restores .NET packages, installs frontend dependencies and stores development settings in .NET User Secrets.

### 2. Start the API and the frontend

From the repository root, open two terminals. In **terminal 1**, start the API and seed a fresh development database.

```powershell
.\Start-Api.cmd -Seed
```

In **terminal 2**, start the React dev server.

```powershell
.\Start-Frontend.cmd
```

Open the [student portal](http://127.0.0.1:5173). Vite forwards `/api` requests to port 5080. To check the API and its MongoDB connection, visit the [health endpoint](http://localhost:5080/api/health) and look for `{"status":"healthy"}`.

### 3. Explore the seeded data

A fresh seed creates **18 student profiles across three departments**. Sign in with the password you chose during setup.

| Account | Try this |
| --- | --- |
| `admin@campus.local` | Open the directory, find a student and review their complete profile. |
| `staff@campus.local` | Open the departmental report and export the CSV. Compare the email and GPA fields with the admin view. |
| `student@campus.local` | Open your profile, update your phone number and reload to see the saved change. |

**On subsequent launches**, run `.\Start-Api.cmd` and `.\Start-Frontend.cmd` in separate terminals. To launch the API directly after setup, use `dotnet run --project backend/StudentRecords.Api --launch-profile StudentRecords.Api`.

## Build and test

From the repository root after setup, use these commands.

| Command | Result |
| --- | --- |
| `npm --prefix frontend test` | Runs the Vitest suite for the React client. |
| `pwsh -File scripts/Test.ps1` | Runs the .NET API and backup tests, the frontend tests and a frontend build. Install MongoDB Community Server locally for the default isolated test run. |
| `pwsh -File scripts/Build.ps1` | Produces a Release API with the compiled React app in `artifacts/publish-*/`. |

The [API tests](tests/StudentRecords.Api.Tests) exercise MVC routing, record scope, CSRF, session handling, upload validation and report masking. The [backup tests](tests/StudentRecords.Backup.Tests) cover archive integrity and restoration of BSON records and files.

### Optional operations setup

Run `.\Setup-Operations.cmd` to configure change streams and scheduled encrypted backups, then restart the API. Change streams require Atlas or a replica set. The scheduler runs while the API is up, and backup coordination covers one API instance. Keep the encryption password separately from the archives for recovery.
