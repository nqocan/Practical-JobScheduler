using JobScheduler.Core.Enums;

namespace JobScheduler.API.Models;

public record CreateJobRequest(JobType Type, object Payload);
