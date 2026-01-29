# TEMPLATE GVDASA BACKEND (.NET)
[[_TOC_]]


GVmodeloExemplo-back

Shorname do template: gv-webapi-cs-nets
---

## Guia Rápido

1. Clone o projeto do template em um diretório a sua escolha

```bash
    git clone https://gvdasa@dev.azure.com/gvdasa/NAP/_git/Template-gv-webapi-cs-net6 # https
    # or
    git clone git@ssh.dev.azure.com:v3/gvdasa/NAP/Template-gv-webapi-cs-net6 # SSH
```
2. Instale o Template

```bash
    # diretório do projeto
    cd Template-gv-webapi-cs-net6

    # instação do template
    dotnet new --install .\
```
3. Criação do projeto a partir do template
Crie um diretório onde seu projeto será instalado, navegue até o diretório e execute o comando abaixo:

```bash
   dotnet new gv-webapi-cs-net {parametros}
```


### Parameters

| ParametroS | Tipo | Default | isRequired | Descrição |
|-------------|-------------|-------------|-------------|-------------|
| appname | STRING | GVproduto | FALSE** | Renomeia arquivos contendo 'GVmodeloexemploapi |
| idproduto | STRING | GVproduto | FALSE** | ID de produto único na GVdasa, que é referenciado no cadastro de produtos do CAC, além de ser utilizado em várias situações como header IdClient |
| cosmosdb | BOOLEAN | FALSE | FALSE | Indica o uso de cosmosdb |
| sqlserver | BOOLEAN | FALSE | FALSE | Indica o uso do MSSQLServer |
| responsibleDev | STRING | GVproduto | FALSE** | Descreve o desenvolvedor responsável |
| responsibleProd | STRING | GVproduto | FALSE** | Descreve o responsável da app produção |
| setor | STRING | GVproduto | FALSE** | Preenche | Descreve o produto no appsettings |
| description | STRING | Modelo de teste para o back | FALSE** | Descreve o produto no appsettings |

**Não são obrigatórias, mas são importantes para a criação do projeto.

### EXEMPLO DE USO
Para criar o projeto (ápos a instalação do template), execute o comando abaixo:

```bash
#exemplo
dotnet new gv-webapi-cs-net --appname GVProduto --idproduto gvproduto --sqlserver true --responsibleDev epravtz@gvdasa.com.br --responsibleProd epravtz@gvdasa.com.br --setor NAP --description 'descrição qualquer do produto'
```

As flags que não forem mencionadas no comando de criação poderão ser revistas quanto ao seu ponto de substituição. Para localizar esses pontos, faça uma busca na sua IDE de preferência pela string <substituir:


### Referências

* [Explicação de como funciona a instalação dos templates](https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-new-install)
* [Como desinstalar um template](https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-new-uninstall)

## Contribuições:
Time do NAP/SB
@<DD1E5123-1E20-6BDC-96E0-1CA31532EE8E> 
@<8E339D48-C929-604B-B657-E3EA4BFB48F6> 