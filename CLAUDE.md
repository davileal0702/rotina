# Rotina

App pessoal que trava o PC Windows e o celular Android na atividade da rotina. A especificação está em `docs/SPEC.md`; a seção "Ajustes decididos" vale por cima do resto. O contrato da API é `docs/api.yaml`.

## Como trabalhar

- Uma fase por vez (ver "Fases de desenvolvimento" no SPEC). Pare ao fim de cada fase para o usuário testar os critérios de aceite.
- Tudo roda em modo desenvolvimento até a fase 6: `minDurationMinutes` conta como segundos.
- Nunca ligue proteções de produção (políticas, Device Owner, restrições) antes da fase 6 e da chave de recuperação.
- Commit ao fim de cada fase, só com o ok do usuário.
- Textos para o usuário em português, sem travessão (—). Código, nomes e comentários em inglês.

## Windows (`windows/`, .NET 10)

- `Rotina.Core`: modelos e o motor de regras (`Engine`), puro, sem I/O. Toda regra de estado, fila, agenda e trava de edição mora aqui e tem teste.
- `Rotina.Service`: serviço do Windows (LocalSystem). Kestrel em `127.0.0.1:47800`, SQLite em `%ProgramData%\Rotina\rotina.db`, ciclo de 1 s que chama `Engine.Tick`.
- `Rotina.App`: WPF. Interface de edição, ícone na bandeja, aviso de bloqueio e o próprio bloqueio de apps (`Blocker`): o serviço roda na sessão 0 e não vê as janelas do usuário, então quem fecha os apps é o App. Instância única; `--background` abre só na bandeja. O serviço reabre o App (`AgentLauncher`) enquanto houver atividade.
- Cuidado ao testar: durante uma atividade, toda janela fora da lista é fechada, inclusive editor, terminal e navegador. Libere o que estiver usando.
- `Rotina.Tests`: xUnit do `Engine`.

Banco: uma tabela `records(kind, id, json, rev)`. Cada entidade é um JSON (camelCase, datas ISO UTC). `rev` cresce a cada escrita, para a sincronização da fase 5. Exclusão é lógica (`deleted`).

Agenda: horários lidos no fuso fixo de `Settings.TimeZoneId`, nunca no fuso atual do sistema. A sessão agendada usa id determinístico (`Engine.ScheduledSessionId`); o Android precisa calcular igual.

Comandos:

```
dotnet test windows/Rotina.Tests
dotnet run --project windows/Rotina.Service
dotnet run --project windows/Rotina.App
```

Instalar como serviço (PowerShell como administrador): `windows/install-service.ps1` (`-Uninstall` remove).

## Extensão do Chrome (`chrome-extension/`, Manifest V3)

- `background.js` lê `GET /api/v1/state` e aplica regras dinâmicas de `declarativeNetRequest` (só `main_frame` e `sub_frame`). As regras puras ficam em `rules.js`, testadas com `node --test chrome-extension/rules.test.mjs`.
- Para testar: `chrome://extensions`, ative o modo desenvolvedor, "Carregar sem compactação" e escolha a pasta `chrome-extension`.

## Pendências conhecidas

- Fase 5: abrir a API para a rede com token; hoje só aceita localhost e sem autenticação.
- Fase 6: restringir as permissões da pasta `%ProgramData%\Rotina` (hoje o usuário consegue editar o banco) e detectar saltos no relógio.
