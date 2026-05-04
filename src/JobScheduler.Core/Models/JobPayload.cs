namespace JobScheduler.Core.Models;

public record EmailPayload(string To, string Subject, string Body);

public record ExportPayload(string Entity, string Format, Dictionary<string, string> Filters);

public record SyncPayload(string Source, string Destination);
