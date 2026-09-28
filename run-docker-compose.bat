@echo off
REM Builds and starts Weir with docker-compose.yml + docker-compose.override.yml (merged automatically).
REM Prerequisites:
REM   - The target database exists on your SQL Server (for the demo: run samples\sqlserver\demo-database.sql).
REM   - The connection string and credentials in docker-compose.override.yml are valid.
REM   - SQL Server accepts TCP connections on port 1433 and the Windows firewall allows them.
docker compose up -d --force-recreate --build
echo.
echo Weir API:       http://localhost:8080            (data plane; the surface to publish)
echo Weir admin UI:  http://localhost:8081            (sign in: admin / admin-demo; keep this port private)
echo Readiness:      http://localhost:8080/health/ready   (health answers on both ports)
echo Split ports:    two surfaces on two ports - docs\en\deployment.md, run-docker-compose.bat
echo Logs:           docker compose logs -f weir
echo Stop:           docker compose down
echo.
echo Note: SQL Server is external (your own instance in docker-compose.override.yml), not started here.
pause
