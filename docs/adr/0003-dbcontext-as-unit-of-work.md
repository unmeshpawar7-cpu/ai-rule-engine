# ADR 0003: EF Core DbContext as the unit of work (no generic repositories)

* Status: Accepted
* Date: 2026-10-08

## Context

Use cases change several aggregates and must write an audit record atomically with the change (e.g. publish a
version, retire the previous one, record test runs, write the audit log). Common options: generic repositories plus
a unit-of-work abstraction over EF Core, or using EF Core's `DbContext` (already a unit of work and a set of
repositories) through a narrow interface.

## Decision

Application services depend on `IRuleEngineDbContext` (DbSets + `SaveChangesAsync`). Each use case stages all
changes - including the `AuditLog` entity via `IAuditTrail` - and calls `SaveChangesAsync` once, which EF Core
executes in a single database transaction. Reads use `AsNoTracking` and project to DTOs.

## Consequences

* Atomic audit without explicit transaction code; no repository boilerplate that would hide EF features
  (projections, `Include`, concurrency tokens).
* Optimistic concurrency uses a `ConcurrencyStamp` Guid concurrency token (works on SQL Server and SQLite), plus an
  optional client-supplied `expectedConcurrencyStamp` for explicit 409s.
* The application layer references `Microsoft.EntityFrameworkCore` (for `DbSet<T>` and async LINQ). This is a
  deliberate, contained dependency; unit tests use the EF Core in-memory provider and integration tests SQLite.
* Webhook side effects happen before the commit; a transactional outbox is the documented next step if
  exactly-once delivery is required.
