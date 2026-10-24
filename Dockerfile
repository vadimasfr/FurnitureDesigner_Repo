# Furniture Designer web app (Blazor Server). The WPF desktop app is Windows-only and not part of the image.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore first, so package downloads are cached while only source files change.
COPY src/FurnitureDesigner.Core/FurnitureDesigner.Core.csproj src/FurnitureDesigner.Core/
COPY src/FurnitureDesigner.Ai/FurnitureDesigner.Ai.csproj src/FurnitureDesigner.Ai/
COPY src/FurnitureDesigner.Web/FurnitureDesigner.Web.csproj src/FurnitureDesigner.Web/
RUN dotnet restore src/FurnitureDesigner.Web/FurnitureDesigner.Web.csproj

COPY src/FurnitureDesigner.Core/ src/FurnitureDesigner.Core/
COPY src/FurnitureDesigner.Ai/ src/FurnitureDesigner.Ai/
COPY src/FurnitureDesigner.Web/ src/FurnitureDesigner.Web/
RUN dotnet publish src/FurnitureDesigner.Web/FurnitureDesigner.Web.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
# Listens on 8080 by default; a PORT variable (set by hosts such as Railway) overrides it.
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "FurnitureDesigner.Web.dll"]
