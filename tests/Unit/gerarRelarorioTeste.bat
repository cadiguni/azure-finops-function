@echo off

REM Verifica se o dotnet-reportgenerator-globaltool está instalado
dotnet tool list -g | findstr /C:"dotnet-reportgenerator-globaltool" > nul
if %errorlevel% neq 0 (
    dotnet tool install -g dotnet-reportgenerator-globaltool
)

REM Exclui a pasta TestResults
IF EXIST "TestResults" (
    rd /s /q "TestResults"
)

REM Executa o comando dotnet test --collect "XPlat Code Coverage" criando a pasta TestResults
dotnet test --collect "XPlat Code Coverage"

REM Cria diretório destino, caso não exista, e exclui seu conteúdo
if not exist "Relatorio" (
    mkdir "Relatorio" )
del /q "Relatorio\*"

REM Move o xml gerado para a pasta relatorio
set "diretorio_origem=TestResults"
set "diretorio_destino=Relatorio"

REM Percorre recursivamente os subdiretórios e move os arquivos XML
for /r "%diretorio_origem%" %%f in (*.xml) do (
    move "%%f" "%diretorio_destino%"
)
rd /s /q "TestResults"

REM Gera o relatório de cobertura de testes
reportgenerator -reports:.\Relatorio\coverage.cobertura.xml -targetdir:.\Relatorio

REM Abre o navegador com o relatório
start "" "Relatorio\Index.html"
