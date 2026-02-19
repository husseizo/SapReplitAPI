# -----------------------------------
# Build Stage
# -----------------------------------
FROM mcr.microsoft.com/dotnet/sdk:8.0-windowsservercore-ltsc2022 AS build
WORKDIR /app

COPY *.csproj ./
RUN dotnet restore

COPY . ./
RUN dotnet publish -c Release -o out

# -----------------------------------
# Runtime Stage
# -----------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:8.0-windowsservercore-ltsc2022 AS final
WORKDIR /app

# Optional: copy Ngrok binary and config (must be placed correctly on host first)
COPY Ngrok/ngrok.exe C:/ngrok.exe
COPY Ngrok/ngrok.yml C:/Users/ContainerUser/.ngrok2/ngrok.yml

# Copy published output
COPY --from=build /app/out ./

# Optional: expose ports
EXPOSE 7121

# Entry point to run both your app and ngrok
CMD powershell -Command "Start-Process -NoNewWindow -FilePath 'dotnet' -ArgumentList 'SapReplitAPI.dll'; Start-Process -NoNewWindow -FilePath 'C:/ngrok.exe' -ArgumentList 'start --all --config C:/Users/ContainerUser/.ngrok2/ngrok.yml'; Wait-Process -Name dotnet"