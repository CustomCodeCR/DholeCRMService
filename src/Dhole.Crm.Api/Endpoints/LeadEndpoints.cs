using Dhole.Crm.Domain;
using Dhole.Crm.Infrastructure.Persistence;
using Dhole.Crm.Infrastructure.References;
using Dhole.Crm.Infrastructure.Realtime;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Dhole.Crm.Api.Endpoints;

public sealed record CreateLeadRequest(string Name, string? CompanyName, Guid? CountryId, string? TaxId,
    string? SourceCode, string? RelationshipTypeCode, Guid? ExecutiveUserId,
    string? ContactEmail, string? Phone, bool AssignToCastroFallas = false,
    string? SourceDetail = null, string? InitialNotes = null,
    List<CreateContactRequest>? Contacts = null, List<AddressRequest>? Addresses = null);
public sealed record PatchLeadRequest(long ExpectedVersion, string Name, string? CompanyName, string? SourceCode,
    string? RelationshipTypeCode, string QualificationStatus, string? ContactEmail, string? Phone,
    string? SourceDetail = null, string? InitialNotes = null);
public sealed record ConvertLeadRequest(Guid? ExistingAccountId, string? Notes,
    string? TaxIdTypeCode, string CustomerTypeCode, string CategoryType, string CategoryCode);
public sealed record AssignLeadRequest(long ExpectedVersion, Guid? ExecutiveUserId, bool AssignToCastroFallas = false);

internal static class LeadEndpoints
{
    public static RouteGroupBuilder MapLeads(this RouteGroupBuilder group)
    {
        group.MapGet("/leads", List);
        group.MapGet("/leads/assigned-executives", AssignedExecutives);
        group.MapGet("/leads/realtime-access", (HttpContext http) =>
            CrmDataEndpoints.HasRead(http, "lead") ? Results.Ok() : CrmAccess.Denied());
        group.MapGet("/leads/{id:guid}", Get);
        group.MapGet("/leads/{id:guid}/change-history", ChangeHistory);
        group.MapGet("/leads/{id:guid}/opportunities", ListLeadOpportunities);
        group.MapGet("/leads/{id:guid}/quotes", ListLeadQuotes);
        group.MapPost("/leads", Create);
        group.MapPatch("/leads/{id:guid}", Patch);
        group.MapPost("/leads/{id:guid}/convert", Convert);
        group.MapPost("/leads/{id:guid}/assign", Assign);
        group.MapPost("/leads/{id:guid}/archive", Archive);

        return group;
    }
    private static async Task<IResult> ChangeHistory(Guid id, CrmDbContext db,
        HttpContext http, IAuthReferenceService auth, IMemoryCache cache,
        int? page, int? pageSize, CancellationToken ct)
    {
        if (!CrmDataEndpoints.HasRead(http, "lead")) return CrmAccess.Denied();
        var lead = await db.Leads.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == id && x.ArchivedAt == null, ct);
        if (lead is null) return Results.NotFound();
        if (!CrmAccess.LeadAccess(http, lead.ExecutiveUserId)) return CrmAccess.Denied();

        var currentPage = Math.Max(1, page ?? 1);
        var size = Math.Clamp(pageSize ?? 25, 1, 50);
        var query = db.LeadChangeHistories.AsNoTracking().Where(x => x.LeadId == id);
        var total = await query.CountAsync(ct);
        var entries = await query.OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id)
            .Skip((currentPage - 1) * size).Take(size).ToListAsync(ct);

        // Resolve only users belonging to the authorized page of this prospect.
        var actors = entries.Where(x => x.ActorUserId.HasValue)
            .Select(x => x.ActorUserId!.Value).Distinct().ToArray();
        var actorNames = await Task.WhenAll(actors.Select(async userId =>
        {
            var cacheKey = "crm:lead-audit-actor:" + userId.ToString("N");
            if (!cache.TryGetValue(cacheKey, out string? displayName))
            {
                try
                {
                    var user = await auth.GetActiveAsync(userId, ct);
                    displayName = user?.DisplayName ?? user?.Email ?? "";
                }
                catch (Exception) when (!ct.IsCancellationRequested) { displayName = ""; }
                cache.Set(cacheKey, displayName, TimeSpan.FromMinutes(10));
            }
            return (Id: userId, Name: displayName);
        }));
        var names = actorNames.ToDictionary(x => x.Id, x => x.Name);
        return Results.Ok(new
        {
            total, page = currentPage, pageSize = size,
            items = entries.Select(x => new
            {
                x.Id, x.ActionCode, x.AreaCode, x.ChangesJson, x.CreatedAt,
                x.ActorUserId,
                actorDisplayName = x.ActorUserId.HasValue &&
                    names.TryGetValue(x.ActorUserId.Value, out var displayName) &&
                    !string.IsNullOrWhiteSpace(displayName) ? displayName : null
            })
        });
    }

    private static async Task<IResult> CommitLeadAsync(CrmDbContext db, HttpContext http,
        CancellationToken ct)
    {
        try
        {
            await CrmCollectionRealtimeCommit.SaveLeadAsync(db,
                http.RequestServices.GetRequiredService<IConfiguration>(), http.TraceIdentifier, ct);
            return Results.Ok();
        }
        catch (DbUpdateConcurrencyException) { return CrmDataEndpoints.Conflict("Record modified concurrently"); }
        catch (DbUpdateException) { return CrmDataEndpoints.Conflict("Conflicting or invalid CRM record"); }
    }


    // Resolve only salespeople of prospect records visible to the requesting user.
    // Names are fetched server-to-server from Auth, not from a public user directory.
    private static async Task<IResult> AssignedExecutives(
        string? ids, CrmDbContext db, HttpContext http, IAuthReferenceService auth,
        IMemoryCache cache, ILoggerFactory loggerFactory, CancellationToken ct)
    {
        if (!CrmDataEndpoints.HasRead(http, "lead")) return CrmAccess.Denied();
        var parts = (ids ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is < 1 or > 100 || parts.Any(x => !Guid.TryParse(x, out _)))
            return Results.BadRequest(new { error = "Prospect ids must be 1 to 100 valid GUIDs" });

        var requested = parts.Select(Guid.Parse).Distinct().ToArray();
        var query = db.Leads.AsNoTracking()
            .Where(x => x.ArchivedAt == null && requested.Contains(x.Id));
        if (!CrmAccess.Scope(http, "crm.lead.read-all"))
            query = query.Where(x => x.ExecutiveUserId == CrmAccess.UserId(http));
        var visible = await query.Select(x => new { x.Id, x.ExecutiveUserId, x.IsCastroFallasExecutive }).ToArrayAsync(ct);
        var sellerIds = visible.Where(x => !x.IsCastroFallasExecutive && x.ExecutiveUserId != Guid.Empty)
            .Select(x => x.ExecutiveUserId).Distinct().ToArray();

        using var gate = new SemaphoreSlim(4);
        var logger = loggerFactory.CreateLogger("Dhole.Crm.LeadAssignedExecutives");
        var names = await Task.WhenAll(sellerIds.Select(async userId =>
        {
            await gate.WaitAsync(ct);
            try
            {
                var name = await cache.GetOrCreateAsync($"crm:assigned-seller:{userId:N}", async entry =>
                {
                    entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(2);
                    var person = await auth.GetActiveAsync(userId, ct);
                    return string.IsNullOrWhiteSpace(person?.DisplayName)
                        ? "Vendedor no disponible" : person.DisplayName.Trim();
                });
                return (Id: userId, Name: name ?? "Vendedor no disponible");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not resolve assigned CRM prospect seller");
                return (Id: userId, Name: "Vendedor no disponible");
            }
            finally { gate.Release(); }
        }));
        var resolved = names.ToDictionary(x => x.Id, x => x.Name);
        var sellers = visible.ToDictionary(x => x.Id.ToString("D"),
            x => x.IsCastroFallasExecutive ? "Castro Fallas"
                : x.ExecutiveUserId == Guid.Empty ? "Sin asignar"
                : resolved.TryGetValue(x.ExecutiveUserId, out var name)
                    ? name : "Vendedor no disponible");
        return Results.Ok(new { sellers });
    }

    private static async Task<IResult> List(CrmDbContext db, HttpContext http,
        string? q, int? page, int? pageSize, CancellationToken ct)
    {
        if (!CrmDataEndpoints.HasRead(http, "lead")) return CrmAccess.Denied();
        var query = db.Leads.AsNoTracking().Where(x => x.ArchivedAt == null);
        if (!CrmAccess.Scope(http, "crm.lead.read-all"))
            query = query.Where(x => x.ExecutiveUserId == CrmAccess.UserId(http));
        if (!string.IsNullOrWhiteSpace(q))
            query = query.Where(x => x.Name.ToLower().Contains(q.ToLower()) ||
                                     (x.CompanyName != null && x.CompanyName.ToLower().Contains(q.ToLower())));
        var total = await query.CountAsync(ct);
        var number = Math.Max(1, page ?? 1);
        var size = Math.Clamp(pageSize ?? 25, 1, 100);
        return Results.Ok(new { items = await query.OrderBy(x => x.Name).ThenBy(x => x.Id)
            .Skip((number - 1) * size).Take(size).ToListAsync(ct), total, page = number, pageSize = size });
    }
    private static async Task<IResult> Get(Guid id, CrmDbContext db, HttpContext http, CancellationToken ct)
    {
        if (!CrmDataEndpoints.HasRead(http, "lead")) return CrmAccess.Denied();
        var lead = await db.Leads.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id && x.ArchivedAt == null, ct);
        if (lead is null) return Results.NotFound();
        return CrmAccess.LeadAccess(http, lead.ExecutiveUserId) ? Results.Ok(lead) : CrmAccess.Denied();
    }

    private static async Task<IResult> ListLeadOpportunities(Guid id, CrmDbContext db,
        HttpContext http, CancellationToken ct)
    {
        if (!CrmDataEndpoints.HasRead(http, "lead") ||
            !CrmDataEndpoints.HasRead(http, "opportunity")) return CrmAccess.Denied();
        var lead = await db.Leads.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == id && x.ArchivedAt == null, ct);
        if (lead is null) return Results.NotFound();
        if (!CrmAccess.LeadAccess(http, lead.ExecutiveUserId)) return CrmAccess.Denied();
        var opportunities = db.Opportunities.AsNoTracking()
            .Where(x => x.LeadId == id && x.ArchivedAt == null);
        if (!CrmAccess.Scope(http, "crm.opportunity.read-all"))
            opportunities = opportunities.Where(x => x.ExecutiveUserId == CrmAccess.UserId(http));
        return Results.Ok(await opportunities.OrderByDescending(x => x.CreatedAt)
            .Select(x => new { x.Id, x.Title, x.Status, x.ExecutiveUserId })
            .ToListAsync(ct));
    }

    private static async Task<IResult> ListLeadQuotes(Guid id, CrmDbContext db,
        HttpContext http, int? page, int? pageSize, CancellationToken ct)
    {
        if (!CrmDataEndpoints.HasRead(http, "lead") ||
            !CrmDataEndpoints.HasRead(http, "opportunity") ||
            !CrmAccess.Scope(http, "crm.quote.read")) return CrmAccess.Denied();
        var lead = await db.Leads.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == id && x.ArchivedAt == null, ct);
        if (lead is null) return Results.NotFound();
        if (!CrmAccess.LeadAccess(http, lead.ExecutiveUserId)) return CrmAccess.Denied();
        var opportunities = db.Opportunities.AsNoTracking()
            .Where(x => x.LeadId == id && x.ArchivedAt == null);
        if (!CrmAccess.Scope(http, "crm.opportunity.read-all"))
            opportunities = opportunities.Where(x => x.ExecutiveUserId == CrmAccess.UserId(http));
        var linked = from opportunity in opportunities
                     join quote in db.OpportunityQuotes.AsNoTracking()
                         on opportunity.Id equals quote.OpportunityId
                     select new {
                         quote.Id, quote.PricingQuoteId, quote.PricingRequestId,
                         quote.QuoteNumberSnapshot, quote.CommercialStatusSnapshot,
                         quote.ShipmentModeSnapshot, quote.LinkedAt, quote.LastSyncAt,
                         opportunityId = opportunity.Id, opportunityTitle = opportunity.Title
                     };
        var number = Math.Max(1, page ?? 1);
        var size = Math.Clamp(pageSize ?? 25, 1, 100);
        var total = await linked.CountAsync(ct);
        return Results.Ok(new {
            items = await linked.OrderByDescending(x => x.LinkedAt)
                .Skip((number - 1) * size).Take(size).ToListAsync(ct),
            total, page = number, pageSize = size
        });
    }

    private static async Task<IResult> Create(CreateLeadRequest request, CrmDbContext db,
        HttpContext http, IAuthReferenceService auth, IConfigReferenceService config, CancellationToken ct)
    {
        if (!CrmAccess.Scope(http, "crm.lead.create")) return CrmAccess.Denied();
        var owner = CrmAccess.UserId(http);
        if (request.AssignToCastroFallas && request.ExecutiveUserId.HasValue)
            return CrmDataEndpoints.Bad("Select either Castro Fallas or an Auth executive");
        var executiveId = request.AssignToCastroFallas ? owner : request.ExecutiveUserId ?? owner;
        if ((request.AssignToCastroFallas || executiveId != owner) &&
            !CrmAccess.Scope(http, "crm.lead.assign")) return CrmAccess.Denied();
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 220 ||
            !CrmDataEndpoints.IsEmailValid(request.ContactEmail) ||
            request.ContactEmail?.Length > 254 || request.Phone?.Length > 60)
            return CrmDataEndpoints.Bad("Invalid lead identity/contact");
        if (!await CrmDataEndpoints.ValidCountry(config, request.CountryId, ct))
            return CrmDataEndpoints.Bad("Unknown country");
        if (request.SourceCode?.Length > 120 || request.SourceDetail?.Length > 500 ||
            request.InitialNotes?.Length > 5000)
            return CrmDataEndpoints.Bad("Invalid prospect capture notes");
        var newContacts = request.Contacts ?? [];
        var newAddresses = request.Addresses ?? [];
        if (newContacts.Count > 20 || newAddresses.Count > 20)
            return CrmDataEndpoints.Bad("At most 20 contacts and 20 locations are allowed");
        if (newContacts.Count > 0 && !CrmAccess.Scope(http, "crm.contact.create"))
            return CrmAccess.Denied();
        if (newAddresses.Count > 0 && !CrmAccess.Scope(http, "crm.lead.update"))
            return CrmAccess.Denied();
        if (newContacts.Count(c => c.IsPrimary) > 1)
            return CrmDataEndpoints.Bad("Only one primary prospect contact is allowed");
        foreach (var c in newContacts)
        {
            if (string.IsNullOrWhiteSpace(c.FirstName) || string.IsNullOrWhiteSpace(c.LastName) ||
                c.FirstName.Length > 120 || c.LastName.Length > 120 ||
                c.JobTitle?.Length > 220 || c.Phone?.Length > 60 ||
                c.Email?.Length > 254 || !CrmDataEndpoints.IsEmailValid(c.Email) ||
                !await CrmDataEndpoints.ValidCountry(config, c.CountryId ?? request.CountryId, ct))
                return CrmDataEndpoints.Bad("Invalid prospect contact");
        }
        foreach (var a in newAddresses)
        {
            if (a.ContactId is not null || a.AddressText?.Length > 4000 ||
                string.IsNullOrWhiteSpace(a.AddressType) || a.AddressType.Length > 40 ||
                (string.IsNullOrWhiteSpace(a.AddressText) && string.IsNullOrWhiteSpace(a.Admin1Name)) ||
                a.Location?.Length > 2000 || a.CountryCode?.Length > 2 ||
                new[] { a.Admin1Code, a.Admin1Name, a.Admin2Code, a.Admin2Name, a.Admin3Code, a.Admin3Name }
                    .Any(x => x is { Length: > 160 }) || a.PostalCode?.Length > 32 ||
                !await CrmDataEndpoints.ValidCountry(config, a.CountryId ?? request.CountryId, ct))
                return CrmDataEndpoints.Bad("Invalid prospect location");
        }
        if (await auth.GetActiveAsync(executiveId, ct) is null)
            return CrmDataEndpoints.Bad("Unknown or inactive executive");
        // Check the entire CRM collection, never only the requesting seller's visible leads.
        // Advisory transaction locks prevent concurrent POSTs from creating the same prospect.
        var normalizedName = LeadDuplicateIdentity.NormalizeName(request.Name);
        var normalizedCompany = LeadDuplicateIdentity.NormalizeName(request.CompanyName);
        var normalizedTax = LeadDuplicateIdentity.NormalizeTax(request.TaxId);
        var compactNames = new[] { normalizedName.Replace(" ", ""), normalizedCompany.Replace(" ", "") }
            .Where(x => x.Length > 0).Distinct().ToArray();
        await using var duplicateGuard = await db.Database.BeginTransactionAsync(ct);
        foreach (var key in LeadDuplicateIdentity.LockKeys(request.Name, request.CompanyName, request.CountryId, request.TaxId))
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_advisory_xact_lock(842711, hashtext({key}))", ct);

        var candidates = await db.Leads.AsNoTracking()
            .Where(x => x.ArchivedAt == null &&
                (!request.CountryId.HasValue || x.CountryId == null || x.CountryId == request.CountryId) &&
                (compactNames.Contains(x.Name.Replace(" ", "").ToUpper()) ||
                 (x.CompanyName != null && compactNames.Contains(x.CompanyName.Replace(" ", "").ToUpper())) ||
                 (normalizedTax.Length > 0 && x.TaxId != null &&
                  x.TaxId.Replace(" ", "").Replace("-", "").Replace(".", "").ToUpper() == normalizedTax)))
            .OrderBy(x => x.CreatedAt).ToListAsync(ct);
        var duplicate = candidates.FirstOrDefault(x => LeadDuplicateIdentity.Matches(
            x, request.Name, request.CompanyName, request.CountryId, request.TaxId));
        if (duplicate is not null)
        {
            // Optional durable system notification to the person attempting creation.
            // Delivery failures never turn an identity conflict into a successful insert.
            var notificationsEnabled = http.RequestServices.GetRequiredService<IConfiguration>()
                .GetValue("Features:CrmBackgroundTasksEnabled", false);
            if (notificationsEnabled)
            {
                try
                {
                    var now = DateTime.UtcNow;
                    // Throttle persistent inbox alerts; the HTTP response/toast is immediate on every attempt.
                    var tenMinuteBucket = now.ToString("yyyyMMddHH") + (now.Minute / 10);
                    var dedupeKey = $"crmleaddup:{duplicate.Id:N}:{owner:N}:{tenMinuteBucket}";
                    if (!await db.Reminders.AsNoTracking().AnyAsync(x => x.DedupeKey == dedupeKey, ct))
                    {
                        var alert = new CrmReminder {
                            EntityType = "Lead", EntityId = duplicate.Id,
                            RecipientUserId = owner, Channel = "System",
                            NotificationType = "crm.lead.duplicate.v1",
                            DedupeKey = dedupeKey, FireAt = now,
                            TargetVersion = duplicate.RowVersion, CreatedByUserId = owner
                        };
                        db.Reminders.Add(alert);
                        db.OutboxMessages.Add(new CrmOutboxMessage {
                            Id = alert.Id, ReminderId = alert.Id, NextAttemptAt = now
                        });
                        await db.SaveChangesAsync(ct);
                    }
                    await duplicateGuard.CommitAsync(ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception)
                {
                    await duplicateGuard.RollbackAsync(CancellationToken.None);
                    // The 409 response stays authoritative even when the alert queue is unavailable.
                }
            }
            else await duplicateGuard.RollbackAsync(ct);
            string sellerName = "Castro Fallas";
            if (!duplicate.IsCastroFallasExecutive)
            {
                try
                {
                    sellerName = (await auth.GetActiveAsync(duplicate.ExecutiveUserId, ct))?.DisplayName?.Trim()
                        ?? "Vendedor no disponible";
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception)
                {
                    sellerName = "Vendedor no disponible";
                }
            }
            return Results.Json(new {
                code = "CRM_LEAD_DUPLICATE",
                message = $"El prospecto '{duplicate.Name}' ya existe y pertenece al vendedor {sellerName}.",
                prospectName = duplicate.Name,
                sellerName,
                existingProspectId = duplicate.Id
            }, statusCode: StatusCodes.Status409Conflict);
        }

        var lead = new Lead { Name = request.Name.Trim().ToUpperInvariant(), CompanyName = request.CompanyName?.Trim().ToUpperInvariant(),
            CountryId = request.CountryId, TaxId = request.TaxId?.Trim(),
            SourceCode = request.SourceCode?.Trim(), SourceDetail = request.SourceDetail?.Trim(),
            InitialNotes = request.InitialNotes?.Trim(),
            RelationshipTypeCode = request.RelationshipTypeCode?.Trim(),
            ExecutiveUserId = executiveId, IsCastroFallasExecutive = request.AssignToCastroFallas,
            ContactEmail = request.ContactEmail?.Trim(),
            Phone = request.Phone?.Trim(), CreatedByUserId = owner };
        db.Leads.Add(lead);
        foreach (var c in newContacts)
        {
            var first = c.FirstName!.Trim();
            var last = c.LastName!.Trim();
            var contact = new Contact {
                FirstName = first, LastName = last, Name = first + " " + last,
                JobTitle = c.JobTitle?.Trim(), CountryId = c.CountryId ?? request.CountryId,
                Email = c.Email?.Trim().ToLowerInvariant(), Phone = c.Phone?.Trim(),
                CreatedByUserId = owner
            };
            db.Contacts.Add(contact);
            db.LeadContacts.Add(new LeadContact {
                LeadId = lead.Id, ContactId = contact.Id,
                RelationRole = string.IsNullOrWhiteSpace(c.RelationRole) ? "CONTACT" : c.RelationRole.Trim(),
                IsPrimary = c.IsPrimary, IsOperationsContact = c.IsOperationsContact
            });
        }
        foreach (var a in newAddresses)
        {
            var iso = a.CountryCode?.Trim().ToUpperInvariant();
            var locationText = string.IsNullOrWhiteSpace(a.AddressText)
                ? string.Join(", ", new[] { a.Admin3Name, a.Admin2Name, a.Admin1Name }
                    .Where(x => !string.IsNullOrWhiteSpace(x))) : a.AddressText.Trim();
            db.LeadAddresses.Add(new LeadAddress {
                LeadId = lead.Id, CountryId = a.CountryId ?? request.CountryId,
                AddressType = a.AddressType.Trim(), AddressText = locationText,
                Location = a.Location?.Trim(), CountryCode = iso,
                Admin1Code = a.Admin1Code?.Trim(), Admin1Name = a.Admin1Name?.Trim(),
                Admin2Code = a.Admin2Code?.Trim(), Admin2Name = a.Admin2Name?.Trim(),
                Admin3Code = a.Admin3Code?.Trim(), Admin3Name = a.Admin3Name?.Trim(),
                PostalCode = CrmGeoEndpoints.PostalCodeForCostaRica(iso,
                    a.Admin1Code, a.Admin2Code, a.Admin3Code) ?? a.PostalCode?.Trim(),
                CreatedByUserId = owner
            });
        }
        await CrmCollectionRealtimeCommit.SaveLeadAsync(db,
            http.RequestServices.GetRequiredService<IConfiguration>(), http.TraceIdentifier, ct);
        await duplicateGuard.CommitAsync(ct);
        return Results.Created("/api/crm/leads/" + lead.Id, lead);
    }
    private static async Task<IResult> Patch(Guid id, PatchLeadRequest request, CrmDbContext db,
        HttpContext http, CancellationToken ct)
    {
        if (!CrmAccess.Scope(http, "crm.lead.update")) return CrmAccess.Denied();
        var lead = await db.Leads.FindAsync([id], ct);
        if (lead is null || lead.ArchivedAt != null) return Results.NotFound();
        if (lead.ExecutiveUserId != CrmAccess.UserId(http) && !CrmAccess.Scope(http, "crm.lead.assign"))
            return CrmAccess.Denied();
        if (lead.ConvertedAt is not null) return CrmDataEndpoints.Conflict("Converted leads are immutable");
        if (lead.RowVersion != request.ExpectedVersion) return CrmDataEndpoints.Conflict("Stale row version");
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 220 ||
            !CrmDataEndpoints.IsEmailValid(request.ContactEmail) ||
            request.SourceCode?.Length > 120 || request.SourceDetail?.Length > 500 ||
            request.InitialNotes?.Length > 5000)
            return CrmDataEndpoints.Bad("Invalid lead");
        lead.Name = request.Name.Trim().ToUpperInvariant();
        lead.CompanyName = request.CompanyName?.Trim().ToUpperInvariant();
        lead.SourceCode = request.SourceCode?.Trim();
        if (request.SourceDetail is not null) lead.SourceDetail = request.SourceDetail.Trim();
        if (request.InitialNotes is not null) lead.InitialNotes = request.InitialNotes.Trim();
        lead.RelationshipTypeCode = request.RelationshipTypeCode?.Trim();
        lead.QualificationStatus = request.QualificationStatus;
        lead.ContactEmail = request.ContactEmail?.Trim();
        lead.Phone = request.Phone?.Trim();
        lead.UpdatedAt = DateTime.UtcNow;
        lead.UpdatedByUserId = CrmAccess.UserId(http);
        lead.RowVersion++;
        var committed = await CommitLeadAsync(db, http, ct);
        return committed is Microsoft.AspNetCore.Http.HttpResults.Ok ? Results.Ok(lead) : committed;
    }
    private static async Task<IResult> Assign(Guid id, AssignLeadRequest request, CrmDbContext db, HttpContext http,
        IAuthReferenceService auth, CancellationToken ct)
    {
        if (!CrmAccess.Scope(http, "crm.lead.assign")) return CrmAccess.Denied();
        var lead = await db.Leads.FindAsync([id], ct);
        if (lead is null || lead.ArchivedAt != null) return Results.NotFound();
        if (lead.RowVersion != request.ExpectedVersion) return CrmDataEndpoints.Conflict("Stale row version");
        if (lead.ConvertedAt is not null) return CrmDataEndpoints.Conflict("Converted lead cannot be reassigned");
        if (request.AssignToCastroFallas && request.ExecutiveUserId.HasValue)
            return CrmDataEndpoints.Bad("Select either Castro Fallas or an Auth executive");
        if (!request.AssignToCastroFallas &&
            (!request.ExecutiveUserId.HasValue || request.ExecutiveUserId == Guid.Empty))
            return CrmDataEndpoints.Bad("Valid Auth executive required");
        var executiveId = request.AssignToCastroFallas
            ? CrmAccess.UserId(http) : request.ExecutiveUserId!.Value;
        if (await auth.GetActiveAsync(executiveId, ct) is null)
            return CrmDataEndpoints.Bad("Executive not active in Auth");
        if (lead.ExecutiveUserId == executiveId &&
            lead.IsCastroFallasExecutive == request.AssignToCastroFallas) return Results.Ok(lead);
        lead.ExecutiveUserId = executiveId;
        lead.IsCastroFallasExecutive = request.AssignToCastroFallas;
        lead.UpdatedAt = DateTime.UtcNow;
        lead.UpdatedByUserId = CrmAccess.UserId(http);
        lead.RowVersion++;
        var committed = await CommitLeadAsync(db, http, ct);
        return committed is Microsoft.AspNetCore.Http.HttpResults.Ok ? Results.Ok(lead) : committed;
    }
    private static async Task<IResult> Archive(Guid id, ArchiveAccountRequest request, CrmDbContext db, HttpContext http,
        CancellationToken ct)
    {
        if (!CrmAccess.Scope(http, "crm.lead.archive")) return CrmAccess.Denied();
        if (string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Length > 1000)
            return CrmDataEndpoints.Bad("Archive reason required");
        var lead = await db.Leads.FindAsync([id], ct);
        if (lead is null) return Results.NotFound();
        if (lead.ExecutiveUserId != CrmAccess.UserId(http) && !CrmAccess.Scope(http, "crm.lead.read-all"))
            return CrmAccess.Denied();
        if (lead.RowVersion != request.ExpectedVersion) return CrmDataEndpoints.Conflict("Stale row version");
        if (lead.ConvertedAt is not null) return CrmDataEndpoints.Conflict("Converted lead cannot be archived");
        lead.ArchivedAt = DateTime.UtcNow;
        lead.UpdatedAt = DateTime.UtcNow;
        lead.UpdatedByUserId = CrmAccess.UserId(http);
        lead.RowVersion++;
        var committed = await CommitLeadAsync(db, http, ct);
        return committed is Microsoft.AspNetCore.Http.HttpResults.Ok ? Results.Ok(lead) : committed;
    }
    private static async Task<IResult> Convert(Guid id, ConvertLeadRequest request, CrmDbContext db,
        HttpContext http, IConfigReferenceService config, CancellationToken ct)
    {
        if (!CrmAccess.Scope(http, "crm.lead.convert") || !CrmAccess.Scope(http, "crm.account.create"))
            return CrmAccess.Denied();
        await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, ct);
        var lead = await db.Leads.FirstOrDefaultAsync(x => x.Id == id && x.ArchivedAt == null, ct);
        if (lead is null) return Results.NotFound();
        if (lead.ExecutiveUserId != CrmAccess.UserId(http) && !CrmAccess.Scope(http, "crm.lead.assign"))
            return CrmAccess.Denied();
        if (lead.ConvertedAt is not null)
            return Results.Ok(new { leadId = id, accountId = lead.ConvertedAccountId, alreadyConverted = true });
        if (request.ExistingAccountId is not null)
        {
            var matched = await db.Accounts.AsNoTracking().FirstOrDefaultAsync(
                x => x.Id == request.ExistingAccountId.Value && x.ArchivedAt == null, ct);
            if (matched is null) return Results.NotFound();
            if (!await CrmAccess.CanSeeAccount(db, http, matched.Id, ct)) return CrmAccess.Denied();
            // Explicit operator-selected linkage, never fuzzy automatic matching.
            // Require a matching fiscal identity when one exists; no-tax cases require
            // the elevated account assignment scope in addition to own-record access.
            var leadTax = CrmDataEndpoints.NormalizeTax(lead.TaxId);
            if (leadTax is not null &&
                (matched.CountryId != lead.CountryId || matched.NormalizedTaxId != leadTax ||
                 matched.TaxIdTypeCode != request.TaxIdTypeCode))
                return CrmDataEndpoints.Conflict("Existing account fiscal identity does not match prospect");
            if (leadTax is null && !CrmAccess.Scope(http, "crm.account.assign"))
                return CrmAccess.Denied();
            var currentUser = CrmAccess.UserId(http);
            await CopyProspectRelations(db, id, matched.Id, currentUser, ct);
            lead.ConvertedAt = DateTime.UtcNow;
            lead.ConvertedAccountId = matched.Id;
            lead.QualificationStatus = "Converted";
            lead.UpdatedAt = DateTime.UtcNow;
            lead.UpdatedByUserId = currentUser;
            lead.RowVersion++;
            db.LeadConversions.Add(new LeadConversion
            {
                LeadId = id, AccountId = matched.Id, ConvertedBy = currentUser,
                CreatedByUserId = currentUser, Notes = request.Notes
            });
            try
            {
                await CrmCollectionRealtimeCommit.SaveLeadAsync(db,
                    http.RequestServices.GetRequiredService<IConfiguration>(), http.TraceIdentifier, ct);
                await tx.CommitAsync(ct);
                return Results.Ok(new { leadId = id, accountId = matched.Id, alreadyConverted = false });
            }
            catch (DbUpdateException)
            {
                return CrmDataEndpoints.Conflict("Prospect already converted concurrently");
            }
        }
        var categoryCode = CrmDataEndpoints.CategoryOnCreate(http, request.CategoryType, request.CategoryCode);
        if (string.IsNullOrWhiteSpace(request.CustomerTypeCode) ||
            request.CustomerTypeCode is not ("cash" or "credit") ||
            !CrmDataEndpoints.CategoryValid(request.CategoryType, categoryCode))
            return CrmDataEndpoints.Bad("Invalid account classification");
        if (!await CrmDataEndpoints.ValidCountry(config, lead.CountryId, ct))
            return CrmDataEndpoints.Bad("Unknown country");
        var tax = CrmDataEndpoints.NormalizeTax(lead.TaxId);
        if (tax is not null && (lead.CountryId is null || string.IsNullOrWhiteSpace(request.TaxIdTypeCode)))
            return CrmDataEndpoints.Bad("A tax identity needs country and document type");
        if (tax is not null && await db.Accounts.AnyAsync(x => x.CountryId == lead.CountryId &&
            x.TaxIdTypeCode == request.TaxIdTypeCode && x.NormalizedTaxId == tax && x.ArchivedAt == null, ct))
            return CrmDataEndpoints.Conflict("Duplicate fiscal identity: manual resolution required");
        var owner = CrmAccess.UserId(http);
        var account = new Account
        {
            DisplayName = lead.Name, LegalName = lead.CompanyName ?? lead.Name,
            CountryId = lead.CountryId, TaxId = lead.TaxId,
            NormalizedTaxId = tax, TaxIdTypeCode = request.TaxIdTypeCode,
            CustomerTypeCode = request.CustomerTypeCode, CategoryType = request.CategoryType,
            CategoryCode = categoryCode, SourceCode = lead.SourceCode,
            RelationshipTypeCode = lead.RelationshipTypeCode, ExecutiveUserId = lead.ExecutiveUserId,
            IsCastroFallasExecutive = lead.IsCastroFallasExecutive,
            CreatedByUserId = owner
        };
        db.Accounts.Add(account);
        await CopyProspectRelations(db, id, account.Id, owner, ct);
        db.AccountAssignments.Add(new AccountAssignment { AccountId = account.Id,
            ExecutiveUserId = lead.ExecutiveUserId, IsCastroFallasExecutive = lead.IsCastroFallasExecutive,
            AssignedBy = owner, CreatedByUserId = owner });
        lead.ConvertedAt = DateTime.UtcNow;
        lead.ConvertedAccountId = account.Id;
        lead.QualificationStatus = "Converted";
        lead.RowVersion++;
        lead.UpdatedAt = DateTime.UtcNow;
        lead.UpdatedByUserId = owner;
        db.LeadConversions.Add(new LeadConversion { LeadId = id, AccountId = account.Id,
            ConvertedBy = owner, CreatedByUserId = owner, Notes = request.Notes });
        try
        {
            await CrmCollectionRealtimeCommit.SaveLeadAsync(db,
                http.RequestServices.GetRequiredService<IConfiguration>(), http.TraceIdentifier, ct);
            // Preserve production's fail-closed realtime policy.
            if (CrmAccountRealtimeCommit.IsEnabled(
                    http.RequestServices.GetRequiredService<IConfiguration>()))
                await CrmCollectionRealtimeCommit.AppendCollectionAsync(db, "accountlist",
                    account.RowVersion, http.TraceIdentifier, ct);
            await tx.CommitAsync(ct);
            return Results.Created("/api/crm/accounts/" + account.Id,
                new { leadId = id, accountId = account.Id, alreadyConverted = false });
        }
        catch (DbUpdateException) { return CrmDataEndpoints.Conflict("Concurrent conversion or duplicate fiscal identity"); }
    }

    // Contact IDs are retained: conversion never creates a second person record.
    // These inserts share the serializable account-conversion transaction.
    private static async Task CopyProspectRelations(CrmDbContext db, Guid leadId,
        Guid accountId, Guid actorId, CancellationToken ct)
    {
        var links = await db.LeadContacts.AsNoTracking()
            .Where(x => x.LeadId == leadId && x.ActiveTo == null).ToListAsync(ct);
        var currentLinks = await db.AccountContacts.AsNoTracking()
            .Where(x => x.AccountId == accountId && x.ActiveTo == null).ToListAsync(ct);
        var known = currentLinks.Select(x => x.ContactId).ToHashSet();
        var hasPrimary = currentLinks.Any(x => x.IsPrimary);
        foreach (var link in links.Where(x => !known.Contains(x.ContactId)))
        {
            var primary = link.IsPrimary && !hasPrimary;
            db.AccountContacts.Add(new AccountContact {
                AccountId = accountId, ContactId = link.ContactId,
                RelationRole = link.RelationRole, IsPrimary = primary,
                IsOperationsContact = link.IsOperationsContact
            });
            if (primary) hasPrimary = true;
        }
        var addresses = await db.LeadAddresses.AsNoTracking()
            .Where(x => x.LeadId == leadId).ToListAsync(ct);
        var existing = await db.Addresses.AsNoTracking()
            .Where(x => x.AccountId == accountId)
            .Select(x => new { x.CountryCode, x.AddressText, x.AddressType })
            .ToListAsync(ct);
        foreach (var a in addresses.Where(a => !existing.Any(x =>
            x.AddressType == a.AddressType && x.AddressText == a.AddressText &&
            x.CountryCode == a.CountryCode)))
        {
            db.Addresses.Add(new Address {
                AccountId = accountId, CountryId = a.CountryId, AddressType = a.AddressType,
                AddressText = a.AddressText, Location = a.Location, CountryCode = a.CountryCode,
                Admin1Code = a.Admin1Code, Admin1Name = a.Admin1Name,
                Admin2Code = a.Admin2Code, Admin2Name = a.Admin2Name,
                Admin3Code = a.Admin3Code, Admin3Name = a.Admin3Name,
                PostalCode = a.PostalCode, CreatedByUserId = actorId
            });
        }
    }
}
