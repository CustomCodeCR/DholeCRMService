using Dhole.Crm.Application.Commercial;
using Dhole.Crm.Domain;
using Dhole.Crm.Infrastructure.Persistence;
using Dhole.Crm.Infrastructure.References;
using Microsoft.EntityFrameworkCore;

namespace Dhole.Crm.Api.Endpoints;
public sealed record AnalysisCreateRequest(Guid? AccountId,Guid? LeadId,Guid? OpportunityId,
    Guid? OwnerUserId,string MarketCode,bool CustomsNationalizationRequired,
    string? Observations,decimal? EstimatedMonthlyVolume,string? VolumeUnit);
public sealed record AnalysisPatchRequest(long ExpectedVersion,string MarketCode,
    bool CustomsNationalizationRequired,string? Observations,decimal? EstimatedMonthlyVolume,
    string? VolumeUnit,DateTime? ReviewedAt);
public sealed record AnalysisRouteRequest(string? PolCode,string? PoeCode,string? PodCode,
    string? OriginCountryCode,string? DestinationCountryCode,decimal? FrequencyPerMonth,string? Notes);
public sealed record AnalysisRoutePatchRequest(long ExpectedVersion,string? PolCode,string? PoeCode,
    string? PodCode,string? OriginCountryCode,string? DestinationCountryCode,decimal? FrequencyPerMonth,string? Notes);
public sealed record AnalysisServiceRequest(string ServiceCode,string ModalityCode,string TransportModeCode,
    bool RequiresCustoms,string? CargoTypeCode,decimal Volume,string VolumeUnit,
    string? ContainerTypeCode,int? EquipmentQuantity,decimal? ExpectedMovementsPerMonth,string? Notes);
public sealed record AnalysisServicePatchRequest(long ExpectedVersion,string ServiceCode,string ModalityCode,
    string TransportModeCode,bool RequiresCustoms,string? CargoTypeCode,decimal Volume,
    string VolumeUnit,string? ContainerTypeCode,int? EquipmentQuantity,
    decimal? ExpectedMovementsPerMonth,string? Notes);
public sealed record TradeProfileRequest(string ServiceCode,string? PreferredOriginCode,string? PreferredDestinationCode,
    string Frequency,decimal ExpectedVolume,string VolumeUnit);
public sealed record TradeProfilePatchRequest(long ExpectedVersion,string ServiceCode,string? PreferredOriginCode,
    string? PreferredDestinationCode,string Frequency,decimal ExpectedVolume,string VolumeUnit,bool IsActive);

internal static class CommercialAnalysisEndpoints
{
    public static RouteGroupBuilder MapCommercialAnalyses(this RouteGroupBuilder g)
    {
        g.MapGet("/analyses",List);
        g.MapGet("/analyses/{id:guid}",Get);
        g.MapPost("/analyses",Create);
        g.MapPatch("/analyses/{id:guid}",Patch);
        g.MapGet("/analyses/{id:guid}/routes",Routes);
        g.MapPost("/analyses/{id:guid}/routes",AddRoute);
        g.MapPatch("/analyses/{id:guid}/routes/{routeId:guid}",PatchRoute);
        g.MapGet("/analyses/{id:guid}/services",Services);
        g.MapPost("/analyses/{id:guid}/services",AddService);
        g.MapPatch("/analyses/{id:guid}/services/{serviceId:guid}",PatchService);
        g.MapGet("/accounts/{id:guid}/trade-profiles",TradeProfiles);
        g.MapPost("/accounts/{id:guid}/trade-profiles",AddTradeProfile);
        g.MapPatch("/trade-profiles/{id:guid}",PatchTradeProfile);
        return g;
    }
    private static async Task<CommercialAnalysis?> Allowed(Guid id,CrmDbContext db,HttpContext h,CancellationToken ct)
    {
        var a=await db.CommercialAnalyses.FirstOrDefaultAsync(x=>x.Id==id,ct);
        return a is not null && CommercialAccess.AnalysisOwns(h,a) ? a:null;
    }
    private static async Task<bool> AllowedAccount(Guid accountId,CrmDbContext db,HttpContext h,CancellationToken ct)=>
        await db.Accounts.AsNoTracking().AnyAsync(a=>a.Id==accountId && a.ArchivedAt==null &&
            (a.ExecutiveUserId==CrmAccess.UserId(h) || CrmAccess.Scope(h,"crm.account.read-all")),ct);
    private static bool ValidAnalysis(decimal? amount,string? unit)=>
        (!amount.HasValue||(amount.Value>=0 && amount.Value<=99999999999999.9999m)) &&
        (!amount.HasValue || (!string.IsNullOrWhiteSpace(unit)&&unit.Length<=24));
    private static bool ValidRoute(AnalysisRouteRequest r)=>
        !(string.IsNullOrWhiteSpace(r.PolCode) && string.IsNullOrWhiteSpace(r.PoeCode) &&
          string.IsNullOrWhiteSpace(r.PodCode) && string.IsNullOrWhiteSpace(r.OriginCountryCode) &&
          string.IsNullOrWhiteSpace(r.DestinationCountryCode)) &&
        (!r.FrequencyPerMonth.HasValue||r.FrequencyPerMonth.Value>=0m) &&
        new[]{r.PolCode,r.PoeCode,r.PodCode,r.OriginCountryCode,r.DestinationCountryCode}
            .All(x=>x is null||x.Length<=80);
    private static async Task<bool> ValidRouteCatalogs(AnalysisRouteRequest r,IConfigReferenceService config,CancellationToken ct)
    {
        foreach(var (slug,code) in new []{
            ("pol",r.PolCode),("poe",r.PoeCode),("pod",r.PodCode),
            ("countries",r.OriginCountryCode),("countries",r.DestinationCountryCode)})
            if(!string.IsNullOrWhiteSpace(code) && !await config.IsActiveCodeAsync(slug,code.Trim(),ct))
                return false;
        return true;
    }
    private static bool ValidService(AnalysisServiceRequest r) =>
        !string.IsNullOrWhiteSpace(r.ServiceCode)&&r.ServiceCode.Length<=80 &&
        !string.IsNullOrWhiteSpace(r.ModalityCode)&&r.ModalityCode.Length<=80 &&
        !string.IsNullOrWhiteSpace(r.TransportModeCode)&&r.TransportModeCode.Length<=80 &&
        CommercialRules.ValidVolume(r.Volume,r.VolumeUnit) &&
        (r.EquipmentQuantity is null||r.EquipmentQuantity>0) &&
        (r.ExpectedMovementsPerMonth is null||r.ExpectedMovementsPerMonth>=0) &&
        (r.ContainerTypeCode is null||r.ContainerTypeCode.Length<=80);
    private static async Task<bool> ValidServiceCatalogs(AnalysisServiceRequest r,IConfigReferenceService config,CancellationToken ct)
    {
        if(!await config.IsActiveCodeAsync("pricing-services",r.ServiceCode,ct))return false;
        if(!string.IsNullOrWhiteSpace(r.ContainerTypeCode) &&
           !await config.IsActiveCodeAsync("container-types",r.ContainerTypeCode,ct))return false;
        return true;
    }
    private static async Task<IResult> List(CrmDbContext db,HttpContext h,Guid? accountId,Guid? leadId,
        Guid? opportunityId,int? page,int? pageSize,CancellationToken ct)
    {
        if(!CrmAccess.Scope(h,"crm.analysis.read"))return CrmAccess.Denied();
        var query=db.CommercialAnalyses.AsNoTracking().AsQueryable();
        if(!CrmAccess.Scope(h,"crm.opportunity.read-all") && !CrmAccess.Scope(h,"crm.account.read-all"))
            query=query.Where(x=>x.OwnerUserId==CrmAccess.UserId(h));
        if(accountId is { } a)query=query.Where(x=>x.AccountId==a);
        if(leadId is { } l)query=query.Where(x=>x.LeadId==l);
        if(opportunityId is { } o)query=query.Where(x=>x.OpportunityId==o);
        var number=Math.Max(1,page??1);var size=Math.Clamp(pageSize??25,1,100);
        var total=await query.CountAsync(ct);
        return Results.Ok(new {total,page=number,pageSize=size,items=await query
            .OrderByDescending(x=>x.CreatedAt).Skip((number-1)*size).Take(size).ToListAsync(ct)});
    }
    private static async Task<IResult> Get(Guid id,CrmDbContext db,HttpContext h,CancellationToken ct)
    {
        if(!CrmAccess.Scope(h,"crm.analysis.read"))return CrmAccess.Denied();
        var a=await Allowed(id,db,h,ct);
        return a is null?Results.NotFound():Results.Ok(a);
    }
    private static async Task<IResult> Create(AnalysisCreateRequest r,CrmDbContext db,HttpContext h,
        IAuthReferenceService auth,IConfigReferenceService config,CancellationToken ct)
    {
        if(!CrmAccess.Scope(h,"crm.analysis.manage"))return CrmAccess.Denied();
        if(!ValidAnalysis(r.EstimatedMonthlyVolume,r.VolumeUnit) ||
            !await CommercialAccess.SubjectCanRead(db,h,r.AccountId,r.LeadId,r.OpportunityId,ct) ||
            string.IsNullOrWhiteSpace(r.MarketCode))
            return CommercialAccess.Invalid("Invalid commercial analysis subject or volume");
        if(!await config.IsActiveCodeAsync("crm-markets",r.MarketCode,ct))
            return CommercialAccess.Invalid("Unknown market in CRM Config");
        var current=CrmAccess.UserId(h);var owner=r.OwnerUserId??current;
        if(owner!=current&&!CrmAccess.Scope(h,"crm.opportunity.read-all"))return CrmAccess.Denied();
        if(await auth.GetActiveAsync(owner,ct) is null)return CommercialAccess.Invalid("Analysis owner inactive");
        var a=new CommercialAnalysis {AccountId=r.AccountId,LeadId=r.LeadId,OpportunityId=r.OpportunityId,
            OwnerUserId=owner,MarketCode=r.MarketCode,CustomsNationalizationRequired=r.CustomsNationalizationRequired,
            Observations=r.Observations,EstimatedMonthlyVolume=r.EstimatedMonthlyVolume,VolumeUnit=r.VolumeUnit,
            CreatedByUserId=current};
        db.CommercialAnalyses.Add(a);
        try {await db.SaveChangesAsync(ct);return Results.Created("/api/crm/analyses/"+a.Id,a);}
        catch(DbUpdateException){return CommercialAccess.Conflict("Analysis relationship invalid");}
    }
    private static async Task<IResult> Patch(Guid id,AnalysisPatchRequest r,CrmDbContext db,HttpContext h,
        IConfigReferenceService config,CancellationToken ct)
    {
        if(!CrmAccess.Scope(h,"crm.analysis.manage"))return CrmAccess.Denied();
        var a=await Allowed(id,db,h,ct);if(a is null)return Results.NotFound();
        if(a.RowVersion!=r.ExpectedVersion)return CommercialAccess.Conflict("Stale analysis version");
        if(!ValidAnalysis(r.EstimatedMonthlyVolume,r.VolumeUnit)||
            string.IsNullOrWhiteSpace(r.MarketCode)||
            !await config.IsActiveCodeAsync("crm-markets",r.MarketCode,ct))
            return CommercialAccess.Invalid("Invalid market or estimated volume");
        if(r.ReviewedAt.HasValue && r.ReviewedAt.Value.Kind!=DateTimeKind.Utc)
            return CommercialAccess.Invalid("Review timestamp must be UTC");
        a.MarketCode=r.MarketCode;a.CustomsNationalizationRequired=r.CustomsNationalizationRequired;
        a.Observations=r.Observations;a.EstimatedMonthlyVolume=r.EstimatedMonthlyVolume;
        a.VolumeUnit=r.VolumeUnit;a.ReviewedAt=r.ReviewedAt;a.RowVersion++;
        a.UpdatedAt=DateTime.UtcNow;a.UpdatedByUserId=CrmAccess.UserId(h);
        var save=await CommercialAccess.Save(db,ct);
        return save is Microsoft.AspNetCore.Http.HttpResults.Ok ? Results.Ok(a):save;
    }
    private static async Task<IResult> Routes(Guid id,CrmDbContext db,HttpContext h,CancellationToken ct)
    {
        if(!CrmAccess.Scope(h,"crm.analysis.read")||await Allowed(id,db,h,ct) is null)return CrmAccess.Denied();
        return Results.Ok(await db.CommercialAnalysisRoutes.AsNoTracking().Where(x=>x.AnalysisId==id)
            .OrderBy(x=>x.CreatedAt).ToListAsync(ct));
    }
    private static async Task<IResult> AddRoute(Guid id,AnalysisRouteRequest r,CrmDbContext db,HttpContext h,
        IConfigReferenceService config,CancellationToken ct)
    {
        if(!CrmAccess.Scope(h,"crm.analysis.manage")||await Allowed(id,db,h,ct) is null)
            return CrmAccess.Denied();
        if(!ValidRoute(r)||!await ValidRouteCatalogs(r,config,ct))
            return CommercialAccess.Invalid("Invalid or inactive route references");
        var route=new CommercialAnalysisRoute {AnalysisId=id,PolCode=r.PolCode,PoeCode=r.PoeCode,
            PodCode=r.PodCode,OriginCountryCode=r.OriginCountryCode,
            DestinationCountryCode=r.DestinationCountryCode,FrequencyPerMonth=r.FrequencyPerMonth,
            Notes=r.Notes,CreatedByUserId=CrmAccess.UserId(h)};
        db.CommercialAnalysisRoutes.Add(route);await db.SaveChangesAsync(ct);
        return Results.Created("/api/crm/analyses/"+id+"/routes/"+route.Id,route);
    }
    private static async Task<IResult> PatchRoute(Guid id,Guid routeId,AnalysisRoutePatchRequest r,
        CrmDbContext db,HttpContext h,IConfigReferenceService config,CancellationToken ct)
    {
        if(!CrmAccess.Scope(h,"crm.analysis.manage")||await Allowed(id,db,h,ct) is null)
            return CrmAccess.Denied();
        var route=await db.CommercialAnalysisRoutes.FirstOrDefaultAsync(x=>x.Id==routeId&&x.AnalysisId==id,ct);
        if(route is null)return Results.NotFound();
        if(route.RowVersion!=r.ExpectedVersion)return CommercialAccess.Conflict("Stale route version");
        var next=new AnalysisRouteRequest(r.PolCode,r.PoeCode,r.PodCode,r.OriginCountryCode,
            r.DestinationCountryCode,r.FrequencyPerMonth,r.Notes);
        if(!ValidRoute(next)||!await ValidRouteCatalogs(next,config,ct))
            return CommercialAccess.Invalid("Invalid route");
        route.PolCode=r.PolCode;route.PoeCode=r.PoeCode;route.PodCode=r.PodCode;
        route.OriginCountryCode=r.OriginCountryCode;route.DestinationCountryCode=r.DestinationCountryCode;
        route.FrequencyPerMonth=r.FrequencyPerMonth;route.Notes=r.Notes;route.RowVersion++;
        route.UpdatedAt=DateTime.UtcNow;route.UpdatedByUserId=CrmAccess.UserId(h);
        var save=await CommercialAccess.Save(db,ct);
        return save is Microsoft.AspNetCore.Http.HttpResults.Ok?Results.Ok(route):save;
    }
    private static async Task<IResult> Services(Guid id,CrmDbContext db,HttpContext h,CancellationToken ct)
    {
        if(!CrmAccess.Scope(h,"crm.analysis.read")||await Allowed(id,db,h,ct) is null)return CrmAccess.Denied();
        return Results.Ok(await db.CommercialAnalysisServices.AsNoTracking().Where(x=>x.AnalysisId==id)
            .OrderBy(x=>x.CreatedAt).ToListAsync(ct));
    }
    private static async Task<IResult> AddService(Guid id,AnalysisServiceRequest r,CrmDbContext db,HttpContext h,
        IConfigReferenceService config,CancellationToken ct)
    {
        if(!CrmAccess.Scope(h,"crm.analysis.manage")||await Allowed(id,db,h,ct) is null)return CrmAccess.Denied();
        if(!ValidService(r)||!await ValidServiceCatalogs(r,config,ct))
            return CommercialAccess.Invalid("Invalid or inactive commercial service");
        var service=new CommercialAnalysisService {AnalysisId=id,ServiceCode=r.ServiceCode,
            ModalityCode=r.ModalityCode,TransportModeCode=r.TransportModeCode,RequiresCustoms=r.RequiresCustoms,
            CargoTypeCode=r.CargoTypeCode,Volume=r.Volume,VolumeUnit=r.VolumeUnit,
            ContainerTypeCode=r.ContainerTypeCode,EquipmentQuantity=r.EquipmentQuantity,
            ExpectedMovementsPerMonth=r.ExpectedMovementsPerMonth,Notes=r.Notes,CreatedByUserId=CrmAccess.UserId(h)};
        db.CommercialAnalysisServices.Add(service);await db.SaveChangesAsync(ct);
        return Results.Created("/api/crm/analyses/"+id+"/services/"+service.Id,service);
    }
    private static async Task<IResult> PatchService(Guid id,Guid serviceId,AnalysisServicePatchRequest r,
        CrmDbContext db,HttpContext h,IConfigReferenceService config,CancellationToken ct)
    {
        if(!CrmAccess.Scope(h,"crm.analysis.manage")||await Allowed(id,db,h,ct) is null)return CrmAccess.Denied();
        var s=await db.CommercialAnalysisServices.FirstOrDefaultAsync(x=>x.Id==serviceId&&x.AnalysisId==id,ct);
        if(s is null)return Results.NotFound();
        if(s.RowVersion!=r.ExpectedVersion)return CommercialAccess.Conflict("Stale service version");
        var next=new AnalysisServiceRequest(r.ServiceCode,r.ModalityCode,r.TransportModeCode,r.RequiresCustoms,
            r.CargoTypeCode,r.Volume,r.VolumeUnit,r.ContainerTypeCode,r.EquipmentQuantity,r.ExpectedMovementsPerMonth,r.Notes);
        if(!ValidService(next)||!await ValidServiceCatalogs(next,config,ct))
            return CommercialAccess.Invalid("Invalid service values");
        s.ServiceCode=r.ServiceCode;s.ModalityCode=r.ModalityCode;s.TransportModeCode=r.TransportModeCode;
        s.RequiresCustoms=r.RequiresCustoms;s.CargoTypeCode=r.CargoTypeCode;s.Volume=r.Volume;
        s.VolumeUnit=r.VolumeUnit;s.ContainerTypeCode=r.ContainerTypeCode;
        s.EquipmentQuantity=r.EquipmentQuantity;s.ExpectedMovementsPerMonth=r.ExpectedMovementsPerMonth;
        s.Notes=r.Notes;s.RowVersion++;s.UpdatedAt=DateTime.UtcNow;s.UpdatedByUserId=CrmAccess.UserId(h);
        var save=await CommercialAccess.Save(db,ct);
        return save is Microsoft.AspNetCore.Http.HttpResults.Ok?Results.Ok(s):save;
    }
    private static bool ValidTrade(TradeProfileRequest r) =>
        !string.IsNullOrWhiteSpace(r.ServiceCode)&&r.ServiceCode.Length<=80 &&
        !string.IsNullOrWhiteSpace(r.Frequency)&&r.Frequency.Length<=32 &&
        CommercialRules.ValidVolume(r.ExpectedVolume,r.VolumeUnit);
    private static async Task<IResult> TradeProfiles(Guid id,CrmDbContext db,HttpContext h,CancellationToken ct)
    {
        if(!CrmAccess.Scope(h,"crm.analysis.read")||!await AllowedAccount(id,db,h,ct))
            return CrmAccess.Denied();
        return Results.Ok(await db.CustomerTradeProfiles.AsNoTracking().Where(x=>x.AccountId==id)
            .OrderByDescending(x=>x.CreatedAt).ToListAsync(ct));
    }
    private static async Task<IResult> AddTradeProfile(Guid id,TradeProfileRequest r,
        CrmDbContext db,HttpContext h,IConfigReferenceService config,CancellationToken ct)
    {
        if(!CrmAccess.Scope(h,"crm.analysis.manage")||!await AllowedAccount(id,db,h,ct))
            return CrmAccess.Denied();
        if(!ValidTrade(r)||!await config.IsActiveCodeAsync("pricing-services",r.ServiceCode,ct))
            return CommercialAccess.Invalid("Invalid trade profile or service");
        var p=new CustomerTradeProfile {AccountId=id,ServiceCode=r.ServiceCode,
            PreferredOriginCode=r.PreferredOriginCode,PreferredDestinationCode=r.PreferredDestinationCode,
            Frequency=r.Frequency,ExpectedVolume=r.ExpectedVolume,VolumeUnit=r.VolumeUnit,
            CreatedByUserId=CrmAccess.UserId(h)};
        db.CustomerTradeProfiles.Add(p);await db.SaveChangesAsync(ct);
        return Results.Created("/api/crm/trade-profiles/"+p.Id,p);
    }
    private static async Task<IResult> PatchTradeProfile(Guid id,TradeProfilePatchRequest r,
        CrmDbContext db,HttpContext h,IConfigReferenceService config,CancellationToken ct)
    {
        if(!CrmAccess.Scope(h,"crm.analysis.manage"))return CrmAccess.Denied();
        var p=await db.CustomerTradeProfiles.FindAsync([id],ct);
        if(p is null)return Results.NotFound();
        if(!await AllowedAccount(p.AccountId,db,h,ct))return CrmAccess.Denied();
        if(p.RowVersion!=r.ExpectedVersion)return CommercialAccess.Conflict("Stale profile");
        var next=new TradeProfileRequest(r.ServiceCode,r.PreferredOriginCode,r.PreferredDestinationCode,
            r.Frequency,r.ExpectedVolume,r.VolumeUnit);
        if(!ValidTrade(next)||!await config.IsActiveCodeAsync("pricing-services",r.ServiceCode,ct))
            return CommercialAccess.Invalid("Invalid service/volume");
        p.ServiceCode=r.ServiceCode;p.PreferredOriginCode=r.PreferredOriginCode;
        p.PreferredDestinationCode=r.PreferredDestinationCode;p.Frequency=r.Frequency;
        p.ExpectedVolume=r.ExpectedVolume;p.VolumeUnit=r.VolumeUnit;p.IsActive=r.IsActive;
        p.RowVersion++;p.UpdatedAt=DateTime.UtcNow;p.UpdatedByUserId=CrmAccess.UserId(h);
        var save=await CommercialAccess.Save(db,ct);
        return save is Microsoft.AspNetCore.Http.HttpResults.Ok?Results.Ok(p):save;
    }
}
