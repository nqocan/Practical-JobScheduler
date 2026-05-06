using System.Text.Json;
using JobScheduler.API.Models;
using JobScheduler.Core.Entities;
using JobScheduler.Core.Enums;
using JobScheduler.Core.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace JobScheduler.API.Controllers;

[ApiController]
[Route("jobs")]
public class JobsController(IJobRepository repository) : ControllerBase
{
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateJobRequest request)
    {
        var payload = JsonSerializer.Serialize(request.Payload);
        var job = Job.Create(request.Type, payload);
        await repository.AddAsync(job);
        return CreatedAtAction(nameof(GetById), new { id = job.Id }, JobResponse.From(job));
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetById(Guid id)
    {
        try
        {
            var job = await repository.GetByIdAsync(id);
            return Ok(JobResponse.From(job));
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    [HttpGet]
    public async Task<IActionResult> GetAll([FromQuery] JobStatus? status)
    {
        var jobs = await repository.GetAllAsync(status);
        return Ok(jobs.Select(JobResponse.From));
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Cancel(Guid id)
    {
        try
        {
            var job = await repository.GetByIdAsync(id);
            job.MarkAsCancelled();
            await repository.UpdateAsync(job);
            return NoContent();
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { error = ex.Message });
        }
    }
}
