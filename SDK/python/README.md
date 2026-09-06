# FacilApp SQL SDK para Python

```python
from facilapp_sql import FacilAppSqlClient
api = FacilAppSqlClient()
login = api.login_client_secret("CLIENT_ID", "CLIENT_SECRET")
resultado = api.executar({"funcao": "consultar_sqlserver", "banco": "BANCO", "tabela": "TABELA"})
```

SDK oficial para Python 3.9+, sem dependências externas. Documentação: https://sql.facilapp.com.br/docs/.
