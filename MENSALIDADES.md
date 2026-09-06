# Controle de mensalidades

O cadastro de `console_empresas` possui o campo `tipo_pagamento`:

- `vitalicio`: não exige pagamentos e o login permanece liberado.
- `mensalidade`: exige registro em `console_pagamentos`.
- valor nulo, vazio ou `0`: a API interpreta como `mensalidade`.

Cada registro em `console_pagamentos` guarda a empresa, `data_pagamento`, `valor`, referência opcional e data de criação. A API permite o login por 45 dias completos após o pagamento mais recente. Sem pagamento ou depois desse prazo, os logins locais `/oauth/usuario` e `/executar` com função `login` retornam HTTP `402`:

```json
{
  "ok": false,
  "erro": "falta de pagamento",
  "codigo": "pagamento_pendente"
}
```

## Endpoints

As duas rotas exigem `Authorization: Bearer <token>`.

### Consultar histórico e situação

`GET /api/console/empresas/{cnpj}/pagamentos`

Retorna o tipo de pagamento, histórico, último pagamento e limite de acesso.

### Registrar pagamento

`POST /api/console/empresas/{cnpj}/pagamentos`

```json
{
  "dataPagamento": "2026-09-05T12:00:00-03:00",
  "valor": 100.00,
  "referencia": "PIX setembro/2026"
}
```

Todo login local aceito também gera uma linha física no arquivo diário `Logs/AUDITORIA_yyyyMMdd.jsonl`, com usuário, empresa, CNPJ, sistema, data/hora e origem da chamada.
