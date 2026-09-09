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
                ["attendees"] = new OpenApiArray
                {
                    new OpenApiString("alex@example.com"),
                    new OpenApiString("sarah@example.com")
                },
                ["time_zone"] = new OpenApiString("Asia/Kolkata"),
                ["microsoft_auth"] = new OpenApiString("optional_microsoft_access_token"),
                ["google_auth"] = new OpenApiString("optional_google_access_token")
            };
        }
    }
}
