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
                ["start_time"] = new OpenApiString("2026-09-04T10:00:00Z"),
                ["end_time"] = new OpenApiString("2026-09-04T11:00:00Z"),
                ["description"] = new OpenApiString("Quarterly sprint planning and review."),
                ["attendees"] = new OpenApiArray
                {
                    new OpenApiString("alex@example.com"),
                    new OpenApiString("sarah@example.com")
                },
                ["time_zone"] = new OpenApiString("Asia/Kolkata"),
                ["provider_access_token"] = new OpenApiString("ya29.optional_oauth_token_from_client_sso")
            };
        }
    }
}
