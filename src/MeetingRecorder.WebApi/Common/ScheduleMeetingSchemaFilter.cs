using MeetingRecorder.Application.DTOs;
using Microsoft.OpenApi.Any;
using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace MeetingRecorder.WebApi.Common;

public class ScheduleMeetingSchemaFilter : ISchemaFilter
{
    public void Apply(OpenApiSchema schema, SchemaFilterContext context)
    {
        if (context.Type == typeof(ScheduleMeetingRequest))
        {
            schema.Example = new OpenApiObject
            {
                ["title"] = new OpenApiString("Sprint Planning & Demo"),
                ["provider"] = new OpenApiString("google_meet"),
                ["start_time"] = new OpenApiString("2026-09-04T14:30:00"),
                ["end_time"] = new OpenApiString("2026-09-04T16:30:00"),
                ["description"] = new OpenApiString("Quarterly sprint planning and review."),
                ["header"] = new OpenApiString("Sprint 42 Kickoff & Review"),
                ["mom"] = new OpenApiString("1. Finalized database schema.\n2. Scheduled user onboarding for Friday.\n3. Frontend to integrate schedule API."),
                ["attendees"] = new OpenApiArray
                {
                    new OpenApiString("alex@example.com"),
                    new OpenApiString("sarah@example.com")
                },
                ["time_zone"] = new OpenApiString("Asia/Kolkata"),
                ["recording_id"] = new OpenApiString("3f0e4deb-bd78-49bb-a358-d02f39bd396d"),
                ["summary"] = new OpenApiString("Optional: Pass summary directly or leave empty to auto-load from recording_id"),
                ["microsoft_auth"] = new OpenApiString("optional_microsoft_access_token"),
                ["microsoft_auth_code"] = new OpenApiString("optional_microsoft_one_time_authorization_code"),
                ["microsoft_refresh_token"] = new OpenApiString("optional_microsoft_refresh_token"),
                ["google_auth"] = new OpenApiString("optional_google_access_token"),
                ["google_auth_code"] = new OpenApiString("optional_google_one_time_authorization_code"),
                ["google_refresh_token"] = new OpenApiString("optional_google_refresh_token")
            };
        }
    }
}
