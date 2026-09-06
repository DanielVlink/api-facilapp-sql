"""Cliente oficial, sem dependências externas, para a FacilApp SQL API."""
from __future__ import annotations
import json
from typing import Any
from urllib.error import HTTPError, URLError
from urllib.parse import urlencode
from urllib.request import Request, urlopen

class FacilAppApiError(RuntimeError):
    def __init__(self, status_code: int, response_body: str):
        self.status_code, self.response_body = status_code, response_body
        super().__init__(f"FacilApp SQL API retornou HTTP {status_code}: {response_body}")

class FacilAppSqlClient:
    def __init__(self, base_url: str = "https://sql.facilapp.com.br", timeout: float = 30.0):
        self.base_url, self.timeout, self.access_token = base_url.rstrip("/"), timeout, None

    def status(self) -> dict[str, Any]: return self.request("GET", "/status", authenticated=False)
    def openapi(self) -> dict[str, Any]: return self.request("GET", "/swagger/v1/swagger.json", authenticated=False)

    def login_client_secret(self, client_id: str, client_secret: str, scope: str | None = None) -> dict[str, Any]:
        result = self.request("POST", "/oauth/login-simples", json_body={"client_id": client_id, "client_secret": client_secret, "scope": scope}, authenticated=False)
        self.access_token = result.get("access_token")
        return result

    def oauth_token(self, client_id: str, client_secret: str, scope: str | None = None) -> dict[str, Any]:
        form = urlencode({"grant_type": "client_credentials", "client_id": client_id, "client_secret": client_secret, "scope": scope or ""}).encode()
        result = self.request("POST", "/oauth/token", data=form, content_type="application/x-www-form-urlencoded", authenticated=False)
        self.access_token = result.get("access_token")
        return result

    def executar(self, pedido: dict[str, Any]) -> dict[str, Any]: return self.request("POST", "/executar", json_body=pedido)

    def request(self, method: str, path: str, *, json_body: Any = None, data: bytes | None = None,
                content_type: str = "application/json", authenticated: bool = True) -> Any:
        headers = {"Accept": "application/json"}
        if json_body is not None: data = json.dumps(json_body, ensure_ascii=False).encode("utf-8")
        if data is not None: headers["Content-Type"] = content_type
        if authenticated:
            if not self.access_token: raise RuntimeError("Faça o login ou informe access_token antes da chamada autenticada.")
            headers["Authorization"] = f"Bearer {self.access_token}"
        req = Request(f"{self.base_url}/{path.lstrip('/')}", data=data, headers=headers, method=method.upper())
        try:
            with urlopen(req, timeout=self.timeout) as response:
                raw = response.read().decode("utf-8")
                return json.loads(raw) if raw else None
        except HTTPError as error:
            body = error.read().decode("utf-8", errors="replace")
            raise FacilAppApiError(error.code, body) from error
        except URLError as error:
            raise ConnectionError(f"Não foi possível acessar {self.base_url}: {error.reason}") from error
