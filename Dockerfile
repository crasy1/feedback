# 构建阶段：还原、编译、发布
# 第一步 restore 只用于缓存 NuGet 包下载。发布时不能带 --no-restore：
# 若沿用仅有 .csproj 时生成的还原产物，Blazor 框架脚本
# （_framework/blazor.web.js）不会进入发布产物，页面会因脚本 404 而空白。
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/GameFeedback/GameFeedback.csproj src/GameFeedback/
RUN dotnet restore src/GameFeedback/GameFeedback.csproj
COPY src/ src/
RUN dotnet publish src/GameFeedback -c Release -o /app

# 运行阶段：仅包含运行时与应用
# Npgsql 连接 PostgreSQL 时需要 libgssapi_krb5.so.2；
# 新版 aspnet 基础镜像不再自带，必须显式安装。
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
RUN apt-get update \
    && apt-get install -y --no-install-recommends libgssapi-krb5-2 \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app .
EXPOSE 8080
ENTRYPOINT ["dotnet", "GameFeedback.dll"]
