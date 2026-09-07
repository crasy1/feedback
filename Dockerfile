# 构建阶段：还原、编译、发布
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/GameFeedback/GameFeedback.csproj src/GameFeedback/
RUN dotnet restore src/GameFeedback/GameFeedback.csproj
COPY src/ src/
RUN dotnet publish src/GameFeedback -c Release -o /app --no-restore

# 运行阶段：仅包含运行时与应用
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app .
EXPOSE 8080
ENTRYPOINT ["dotnet", "GameFeedback.dll"]
