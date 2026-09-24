# Build context is the REPO ROOT (see docker-compose.yml) so the shared
# Shop.Contracts project is available to reference.
# --- build stage ---
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY shared/Shop.Contracts/Shop.Contracts.csproj shared/Shop.Contracts/
COPY services/shipping/Shipping.csproj services/shipping/
RUN dotnet restore services/shipping/Shipping.csproj
COPY shared/ shared/
COPY services/shipping/ services/shipping/
RUN dotnet publish services/shipping/Shipping.csproj -c Release -o /app

# --- runtime stage ---
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "Shipping.dll"]
