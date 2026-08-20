# shop133 — Catalog.API (Fase 1.6)
#
# EL CONTEXTO DE BUILD ES LA RAIZ DEL REPO, no esta carpeta. Catalog.API.csproj
# referencia ..\..\..\Shared\Shop133.Contracts, y Docker no puede copiar nada
# que quede por encima del contexto. Por eso todas las rutas de COPY empiezan
# por src/ y por eso el compose declara:
#
#   build:
#     context: .
#     dockerfile: src/Services/Catalog/Catalog.API/Dockerfile
#
# Construir con "docker build ." desde ESTA carpeta falla en el primer COPY.
#
# El Dockerfile vive junto al .csproj de todas formas (convencion de las
# plantillas de .NET y de Visual Studio): es del servicio, no del repo.

# syntax=docker/dockerfile:1


# =========================================================================
# Etapa de build — SDK completo, se descarta al final
# =========================================================================
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# global.json primero, y en su propia capa: fija el SDK (10.0.100 con
# rollForward latestFeature). Sin el, restore y publish usarian el SDK de la
# imagen sin decir nada, y una divergencia con el del host no se notaria hasta
# que el comportamiento difiera.
COPY global.json ./

# Solo los .csproj antes del restore. Esto es lo que hace que tocar un .cs no
# invalide la capa de "dotnet restore": Docker solo reconstruye desde el primer
# COPY cuyo contenido cambio, y estos tres archivos cambian muy poco.
#
# Son exactamente el grafo de Catalog.API: la API referencia Infrastructure y
# Contracts, y ninguno de esos dos referencia nada mas.
COPY src/Shared/Shop133.Contracts/Shop133.Contracts.csproj src/Shared/Shop133.Contracts/
COPY src/Services/Catalog/Catalog.Infrastructure/Catalog.Infrastructure.csproj src/Services/Catalog/Catalog.Infrastructure/
COPY src/Services/Catalog/Catalog.API/Catalog.API.csproj src/Services/Catalog/Catalog.API/

# Restore del .csproj, NO de shop133.slnx: la solucion arrastraria los 10
# proyectos (Gateway, Web, Orders, Payments...) y sus paquetes, para construir
# una imagen que solo necesita tres.
RUN dotnet restore src/Services/Catalog/Catalog.API/Catalog.API.csproj

# Ahora si, el codigo.
COPY src/Shared/Shop133.Contracts/ src/Shared/Shop133.Contracts/
COPY src/Services/Catalog/ src/Services/Catalog/

# --no-restore para que quede claro que el restore ya paso y que esta capa no
# vuelve a tocar la red. Si el restore estuviera incompleto, aqui falla en vez
# de arreglarlo por su cuenta y hacer inutil la separacion de capas.
RUN dotnet publish src/Services/Catalog/Catalog.API/Catalog.API.csproj \
    --configuration Release \
    --no-restore \
    --output /app/publish


# =========================================================================
# Etapa final — solo el runtime de ASP.NET
# =========================================================================
# aspnet:10.0 (Debian) y no -noble-chiseled: la chiseled es mas pequena y ya
# corre sin root, pero no trae shell ni coreutils. Este proyecto existe para
# abrirse y mirarlo por dentro ("docker compose exec catalog-api ls /app" es
# parte de la verificacion de 1.6), y eso alli no se puede.
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app

COPY --from=build /app/publish .

# La imagen base ya trae ASPNETCORE_HTTP_PORTS=8080, asi que no hace falta
# ASPNETCORE_URLS. EXPOSE es documentacion: no publica nada. El mapeo al host
# (5125:8080) vive en docker-compose.override.yml.
EXPOSE 8080

# La imagen base define APP_UID=1654 pero NO aplica el USER: por defecto el
# proceso correria como root. Esta linea es lo que lo evita.
#
# Consecuencia: el proceso no puede escribir en /app ni abrir puertos < 1024.
# Ninguna de las dos cosas hace falta — y que 8080 sea el puerto por defecto de
# estas imagenes es exactamente por esto.
USER $APP_UID

# Sin Database.Migrate() en el arranque (ver Program.cs): esta imagen NUNCA
# crea el esquema. "dotnet ef database update" desde el host es el unico
# camino, y tiene que haberse ejecutado antes del primer arranque.
ENTRYPOINT ["dotnet", "Catalog.API.dll"]
