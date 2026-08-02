# Production multi-stage build
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# Restore with the solution so layer caching works
COPY MeetingRecorder.sln ./
COPY src/MeetingRecorder.Domain/MeetingRecorder.Domain.csproj src/MeetingRecorder.Domain/
COPY src/MeetingRecorder.Application/MeetingRecorder.Application.csproj src/MeetingRecorder.Application/
COPY src/MeetingRecorder.Infrastructure/MeetingRecorder.Infrastructure.csproj src/MeetingRecorder.Infrastructure/
COPY src/MeetingRecorder.WebApi/MeetingRecorder.WebApi.csproj src/MeetingRecorder.WebApi/
COPY tests/MeetingRecorder.UnitTests/MeetingRecorder.UnitTests.csproj tests/MeetingRecorder.UnitTests/
RUN dotnet restore MeetingRecorder.sln

COPY . .
RUN dotnet publish src/MeetingRecorder.WebApi/MeetingRecorder.WebApi.csproj -c Release -o /app/publish

# Runtime image
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS final
WORKDIR /app
COPY --from=build /app/publish .
RUN mkdir -p /app/uploads && chmod 777 /app/uploads

ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    Https__EnableRedirection=false
EXPOSE 8080

ENTRYPOINT ["dotnet", "MeetingRecorder.WebApi.dll"]
