# Regra obrigatória: autoatualização da estrutura da API

Sempre que uma alteração criar, remover ou modificar tabela, campo, tipo, tamanho, chave ou índice do SQLite interno da API (`Dados/facilapp_sql.db`), a alteração deve ser incluída no código de inicialização/migração da própria API (`ConsoleStore.InitializeAsync` ou migração equivalente).

Ao iniciar uma versão nova, a API deve corrigir sozinha o banco já instalado: criar tabelas ausentes, adicionar campos ausentes e aplicar as migrações necessárias, sem tela, PowerShell ou chamada HTTP externa. O instalador preserva os `.db`; portanto a migração no fonte é obrigatória para instalações atualizadas.

O catálogo de campos da versão deve permanecer no fonte (`EnsureCurrentSchemaColumnsAsync`); ao lançar mudança estrutural, incluir a definição da tabela/campo nessa rotina de migração.

`POST /multibanco/estrutura/api` é recurso separado: publica a estrutura da API para bancos externos (`sqlite`, `postgresql`, `sqlserver`, `mysql`, `mariadb`, `oracle` ou `hfsql`). Ele não substitui a migração automática do banco interno.

Antes de publicar alterações estruturais: testar inicialização sobre uma cópia de banco antigo, atualizar Swagger/MD quando necessário e validar o banco resultante.
