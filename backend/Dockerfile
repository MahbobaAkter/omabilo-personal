# Production image for the Omabilo API (UNTESTED: Docker was not running when this was written).
# Build from the REPOSITORY ROOT, because the API compiles shared files from app/Omabilo:
#   docker build -f backend/Dockerfile -t omabilo-api .

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Only the files the API needs: its project plus the shared invoice and demo-data code
COPY app/Omabilo/Models.cs app/Omabilo/Models.cs
COPY app/Omabilo/Invoices/InvoiceModel.cs app/Omabilo/Invoices/InvoiceBook.cs app/Omabilo/Invoices/InvoiceSources.cs app/Omabilo/Invoices/SyntheticInvoices.cs app/Omabilo/Invoices/
COPY backend/src/Omabilo.Api/ backend/src/Omabilo.Api/

RUN dotnet restore backend/src/Omabilo.Api/Omabilo.Api.csproj
RUN dotnet publish backend/src/Omabilo.Api/Omabilo.Api.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .

# The host's HTTPS proxy forwards to this port; set PORT-style variables on the host if it needs another
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080

# Run as the image's built-in non-root user
USER $APP_UID
ENTRYPOINT ["dotnet", "Omabilo.Api.dll"]
