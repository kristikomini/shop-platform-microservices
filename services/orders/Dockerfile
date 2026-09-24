# Build context is the REPO ROOT (see docker-compose.yml) so the shared
# Shop.Contracts project is available to reference.
# --- build stage ---
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY shared/Shop.Contracts/Shop.Contracts.csproj shared/Shop.Contracts/
COPY services/orders/Orders.csproj services/orders/
RUN dotnet restore services/orders/Orders.csproj
COPY shared/ shared/
COPY services/orders/ services/orders/
RUN dotnet publish services/orders/Orders.csproj -c Release -o /app

# --- runtime stage ---
FROM mcr.microsoft.com/dotnet/aspnet:10.0
RUN apt-get update && apt-get install -y --no-install-recommends curl && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "Orders.dll"]
