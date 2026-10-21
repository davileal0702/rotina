# Rotina — Especificação do Projeto

Oct 19, 2026 · @Daniel Leal

## Visão geral

O Rotina é um app pessoal que organiza o dia em atividades programadas e, durante cada atividade, só libera os apps e sites escolhidos para ela, no celular Android e no PC Windows, sincronizados pela rede local.

O objetivo é cumprir a rotina sem distrações: enquanto uma atividade não atinge o tempo mínimo, não dá para encerrá-la nem abrir o que não foi liberado. O usuário é uma pessoa só, com um celular Android e um PC Windows na mesma rede. Não há contas, nuvem nem multiusuário.

**Escopo da v1**

- Cadastro de atividades com tempo mínimo e listas de apps (Android e Windows) e sites liberados
- Agenda semanal recorrente e início manual
- Bloqueio de apps no Android e no Windows
- Bloqueio de sites no Chrome do Windows
- Sincronização entre PC e celular pela rede local
- Proteções contra burla usando permissões de administrador

**Fica para depois**

- Bloqueio de sites no celular (a arquitetura já deve prever isso)
- Edição de atividades pelo celular (na v1 o celular visualiza, inicia e encerra)
- Estatísticas e relatórios (o histórico de sessões já é gravado desde a v1)
- Suporte a outros navegadores além do Chrome (na v1 eles são tratados como apps comuns: bloqueados se não estiverem liberados)

## Regras de funcionamento

Uma atividade em andamento só pode ser encerrada depois do tempo mínimo; fora de atividades, tudo fica liberado.

**Atividade** tem nome, cor, tempo mínimo (em minutos) e três listas de liberados: apps Android, apps Windows e sites. As listas são de permissão: tudo o que não está nelas fica bloqueado durante a atividade.

**Sempre liberados** é uma lista global que vale em qualquer atividade: o próprio Rotina, telefone e chamadas de emergência, a tela inicial e os componentes essenciais do sistema. Ela só pode ser editada fora de atividade.

**Estados**

1. **Livre:** nenhuma atividade ativa, tudo liberado.
2. **Atividade travada:** atividade em andamento sem ter atingido o tempo mínimo. Só o que está liberado abre, e não é possível encerrar nem trocar de atividade.
3. **Atividade concluível:** o tempo mínimo foi atingido. O bloqueio continua, mas o botão Encerrar fica disponível. A atividade segue até ser encerrada ou até a próxima agendada começar.

**Como uma atividade começa**

- **Agendada:** cada atividade pode ter horários na agenda semanal (dias da semana + horário de início). No horário, ela começa sozinha nos dois aparelhos.
- **Manual:** qualquer atividade pode ser iniciada pelo PC ou pelo celular, desde que o estado seja Livre ou Atividade concluível.

**Encerramento:** só depois do tempo mínimo. Ao encerrar, tudo volta a ficar liberado até a próxima atividade.

**Conflitos de horário**

- Se uma atividade agendada chega e a atual já é concluível, a nova substitui a atual.
- Se uma atividade agendada chega e a atual ainda está travada, a nova entra na fila e começa assim que o mínimo da atual for atingido (proposta; ver Decisões em aberto).
- O editor da agenda avisa quando o tempo mínimo de uma atividade invade o início da seguinte.

**Travas de edição durante atividade**

- A atividade em andamento não pode ser editada nem excluída. Mudanças em outras atividades e na agenda são permitidas.
- Cada sessão guarda uma cópia das regras no momento do início, então nada que for editado depois afeta a sessão atual.
- A lista de sempre liberados e as configurações do app ficam travadas enquanto houver atividade travada.

**Contagem do tempo:** o mínimo conta a partir do início real, pelo relógio, inclusive com o aparelho desligado ou reiniciado. Mudanças no relógio do sistema são bloqueadas ou detectadas (ver Proteção contra burla).

**Sessão única:** a atividade é a mesma nos dois aparelhos. Iniciar ou encerrar em um reflete no outro assim que eles sincronizarem.

## Arquitetura

O sistema tem três programas no PC e um app no celular; o serviço do PC é a fonte da verdade, e o celular guarda uma cópia para seguir a agenda fora de casa.

&#91;embedded content: arquitetura · 3 programas no PC, 1 app no celular\]

A interface e a extensão só conversam com o serviço, nunca entre si, e o celular só fala com o PC pela rede local com o token de pareamento.

## Componente Windows

No PC, o Rotina são três programas em C# / .NET (versão LTS atual): um serviço do sistema que manda em tudo, um agente na sessão do usuário e a interface de edição.

**1. Rotina Service (serviço do Windows, roda como LocalSystem)**

- Inicia com o Windows, antes do login, com recuperação automática configurada para reiniciar em caso de falha
- Guarda o banco SQLite, que é a fonte da verdade de atividades, agenda e sessões
- Roda o motor de regras: dispara inícios agendados, controla o tempo mínimo e a fila de conflitos
- Expõe a API HTTP local (Kestrel) usada pela interface, pela extensão do Chrome e pelo celular
- Aplica o bloqueio de apps: a cada \~1 s verifica os processos da sessão do usuário e encerra os que não estão liberados
- Grava as políticas do Chrome no registro e vigia o agente, relançando-o se for fechado

**2. Rotina Agent (processo na sessão do usuário)**

O serviço roda na sessão 0 do Windows e não consegue mostrar janelas para o usuário, por isso existe o agente. Ele mostra o aviso de bloqueio ("Jogo X não está liberado em Estudo · faltam 23 min para poder encerrar") e um ícone na bandeja com a atividade atual e o tempo restante.

**3. Rotina UI (app desktop, WPF ou WinUI 3)**

- Cadastro de atividades, agenda semanal e lista de sempre liberados
- Iniciar e encerrar atividades, com o botão Encerrar desabilitado até o mínimo
- Seleção de apps a partir de um catálogo: apps do Windows (atalhos do Menu Iniciar + processos já vistos) e apps do Android (lista enviada pelo celular)
- Pareamento do celular por QR code

**Regras de bloqueio de apps**

- O app é identificado pelo caminho completo do executável, não só pelo nome, para que renomear um .exe não burle a regra
- Nunca são encerrados processos de sistema (tudo dentro de `C:\Windows`, serviços, o próprio Rotina) nem os da lista de sempre liberados
- A ação padrão é encerrar o processo e mostrar o aviso. Ele pode perder trabalho não salvo (ver Decisões em aberto)
- Fora de atividade, o serviço não interfere em nada

## Extensão do Chrome

Uma extensão Manifest V3 bloqueia no Chrome do PC todo site que não está liberado na atividade atual, consultando o serviço local.

- Lê o estado atual em `http://127.0.0.1:<porta>/api/v1/state` a cada poucos segundos e reage na hora a mudanças de atividade
- Durante uma atividade, aplica regras dinâmicas de `declarativeNetRequest`: tudo é redirecionado para a página de bloqueio, exceto os domínios liberados (com prioridade maior)
- Um site liberado inclui os subdomínios (liberar `youtube.com` libera `m.youtube.com`). Liberar caminhos específicos (ex.: um canal) fica para uma versão futura
- Ao começar uma atividade, as abas já abertas em sites não liberados são redirecionadas
- A página de bloqueio mostra a atividade, o tempo para poder encerrar e a lista de sites liberados
- Envia um sinal de vida (heartbeat) ao serviço a cada poucos segundos. Se o sinal parar durante uma atividade, o serviço entende que a extensão foi desativada e fecha o Chrome

**Políticas do Chrome gravadas pelo serviço** (registro em `HKLM\SOFTWARE\Policies\Google\Chrome`)

- `IncognitoModeAvailability = 1`: desativa a janela anônima, onde a extensão não roda por padrão
- `BrowserGuestModeEnabled = 0` e `BrowserAddPersonEnabled = 0`: impedem abrir perfis sem a extensão
- `ExtensionInstallForcelist`: instala a extensão de forma que não possa ser removida. Em um PC pessoal (fora de domínio corporativo), o Chrome só aceita isso para extensões publicadas na Chrome Web Store; ela pode ser publicada como não listada (ver Decisões em aberto)

O Claude Code deve confirmar os nomes e valores das políticas na documentação atual do Chrome Enterprise antes de implementar.

## App Android

O app Android é nativo, em Kotlin com Jetpack Compose, e bloqueia apps usando um serviço de acessibilidade que detecta o app em primeiro plano.

**Telas**

- **Início:** atividade atual, tempo que falta para poder encerrar, botão Encerrar (habilitado após o mínimo) e a agenda do dia
- **Iniciar atividade:** lista de atividades para início manual
- **Tela de bloqueio:** tela cheia mostrada quando um app não liberado é aberto, com a atividade e o tempo restante, e um botão para voltar à tela inicial
- **Configuração:** pareamento com o PC (leitura do QR code) e checklist de permissões

**Componentes técnicos**

- **AccessibilityService:** escuta mudanças de janela; se o app em primeiro plano não está liberado, abre a tela de bloqueio. O `UsageStatsManager` pode servir de verificação extra
- **Serviço em primeiro plano** com notificação fixa mostrando a atividade atual, para o sistema não matar o app
- **Alarmes exatos** (`AlarmManager.setExactAndAllowWhileIdle`) para os inícios agendados, funcionando com a tela desligada
- **Restauração após reinício:** ao ligar o celular, o app recupera a sessão em andamento
- **Room** para a cópia local de atividades, agenda e sessão, para funcionar sem conexão com o PC
- **Catálogo de apps:** lista os apps instalados (nome, pacote, ícone pequeno) e envia ao PC, para a interface do Windows montar as listas de liberados

**Instalação:** APK instalado direto, sem Play Store. A partir do Android 13, apps instalados assim precisam de um passo extra antes de ativar a acessibilidade: Informações do app → menu ⋮ → Permitir configurações restritas. O app deve guiar esse passo.

**Bloqueio de sites no celular (futuro):** o Chrome do Android não aceita extensões. O caminho recomendado é o próprio serviço de acessibilidade ler a URL na barra de endereço do Chrome e mostrar a tela de bloqueio. Uma VPN local filtrando DNS é a alternativa, mas lista de permissão por DNS costuma quebrar sites que dependem de outros domínios. O código da v1 deve deixar espaço para isso: as regras de site já sincronizam para o celular.

## Sincronização pela rede local

O PC é o centro: guarda a versão oficial de tudo, e o celular busca as mudanças quando está no mesmo Wi-Fi, mantendo uma cópia completa para continuar funcionando fora de casa.

- **Conexão:** o serviço do PC escuta numa porta fixa (ex.: 47800). O celular encontra o PC por mDNS (`_rotina._tcp`), com IP fixo configurável como alternativa. Vale reservar o IP do PC no roteador
- **Pareamento:** a interface do PC mostra um QR code com IP, porta e um token secreto. O celular lê uma vez, e toda requisição passa a levar esse token
- **Firewall:** o instalador cria uma regra de entrada no Firewall do Windows só para redes privadas
- **Frequência:** o celular sincroniza ao conectar no Wi-Fi, a cada 30–60 s enquanto está na rede e imediatamente depois de iniciar ou encerrar uma atividade
- **Eventos:** iniciar e encerrar viram eventos com id único, horário e aparelho de origem. Eventos feitos sem conexão ficam guardados e são enviados quando o celular volta à rede
- **Conflitos:** os eventos são aplicados em ordem de horário, e as regras são validadas dos dois lados. Nenhum evento encerra uma sessão antes do tempo mínimo, venha de onde vier
- **Atividade agendada:** como os dois aparelhos têm a mesma agenda, ela começa em cada um pelo relógio local, mesmo sem conexão
- **Extensão do Chrome:** fala com o serviço só por `127.0.0.1`, na mesma máquina

## Proteção contra burla

Nenhum bloqueio é impossível de burlar por quem é dono do aparelho; a meta é que burlar dê trabalho demais para valer a pena no impulso. Como as Configurações não estão na lista de liberados, boa parte das brechas já é fechada pelo próprio bloqueio de apps.

**Android**

| Brecha | Proteção |
| --- | --- |
| Desinstalar o app | Device Owner: `setUninstallBlocked`. Sem Device Owner, as telas de Configurações ficam bloqueadas durante a atividade |
| Desativar a acessibilidade ou forçar parada | Configurações bloqueadas durante a atividade, inclusive atalhos do painel rápido que abrem telas de configuração |
| Reiniciar o celular | Restauração da sessão ao ligar |
| Modo de segurança | Device Owner: restrição `DISALLOW_SAFE_BOOT` |
| Mudar data e hora | Device Owner: restrição `DISALLOW_CONFIG_DATE_TIME` + detecção de saltos no relógio |
| Restaurar o celular de fábrica | Device Owner: `DISALLOW_FACTORY_RESET` (pelo modo recovery não dá para impedir, e isso apaga tudo de qualquer jeito) |
| Depuração USB / adb | `DISALLOW_DEBUGGING_FEATURES` só no modo produção, para não atrapalhar o desenvolvimento |

Para virar Device Owner num celular já em uso, o comando `adb shell dpm set-device-owner` exige remover temporariamente todas as contas do aparelho (Google e outras), configurar e depois adicioná-las de volta. A alternativa mais simples é Device Admin, que é mais fraco (ver Decisões em aberto).

**Windows**

| Brecha | Proteção |
| --- | --- |
| Fechar o serviço | Roda como LocalSystem; uma conta padrão não consegue pará-lo, e ele reinicia sozinho se falhar |
| Fechar o agente | O serviço relança na hora |
| Gerenciador de Tarefas | Não está liberado durante atividades, então é fechado como qualquer app |
| Mudar data e hora | Exige administrador numa conta padrão |
| Desativar a extensão do Chrome | Instalação forçada por política + heartbeat: sem sinal, o Chrome é fechado |
| Janela anônima, convidado ou outro perfil | Políticas do Chrome |
| Usar outro navegador | Outros navegadores são apps comuns: bloqueados se não estiverem liberados |
| Ser administrador do PC | Ponto fraco: um administrador pode parar o serviço. Recomendação: usar uma conta padrão no dia a dia e guardar a senha da conta administradora fora do seu alcance |

**Rede de segurança**

- **Chave de recuperação:** um código longo gerado na instalação, para ser impresso e guardado com alguém de confiança. Ela desativa todos os bloqueios temporariamente nos dois aparelhos. Como não existe saída de emergência, ela evita que um bug trave o celular ou o PC
- **Modo desenvolvimento:** tempos mínimos em segundos e todas as proteções desligadas, usado até o fim do desenvolvimento
- **Modo produção:** liga as proteções; só pode ser ativado depois que a chave de recuperação for gerada e confirmada

## Modelo de dados e API

O mesmo modelo existe no SQLite do PC e no Room do celular; ids são UUIDs e todo registro tem `updatedAt` e exclusão lógica, para a sincronização funcionar.

| Entidade | Campos principais |
| --- | --- |
| Activity | id, name, color, minDurationMinutes, allowedAndroidPackages\[\], allowedWindowsExecutables\[\], allowedSites\[\], updatedAt, deleted |
| ScheduleEntry | id, activityId, daysOfWeek (dias da semana), startTime (HH:mm), enabled, updatedAt, deleted |
| Session | id, activityId, startedAt, minEndsAt, endedAt?, origin (schedule ou manual), deviceId, rulesSnapshot (cópia das listas no início) |
| SessionEvent | id, sessionId, type (start, end, queued), at, deviceId |
| AlwaysAllowed | platform (android, windows, web), identifier |
| AppCatalogItem | platform, identifier (pacote ou caminho do .exe), label, icon |
| Device | id, name, platform, token, lastSeenAt |
| Settings | mode (desenvolvimento ou produção), recoveryKeyHash |

**API local** (JSON, prefixo `/api/v1`, cabeçalho `Authorization: Bearer <token>` em toda chamada vinda da rede)

| Método e rota | Uso |
| --- | --- |
| `GET /state` | Estado atual: sessão, estado (livre, travada, concluível), segundos restantes e listas efetivas de liberados |
| `GET` / `POST` / `PUT` / `DELETE /activities` | Cadastro de atividades (recusa editar a atividade em andamento) |
| `GET` / `PUT /schedule` | Agenda semanal |
| `POST /sessions/start` | Início manual (`activityId`) |
| `POST /sessions/{id}/end` | Encerramento (recusa antes do mínimo) |
| `GET /sync?since=<rev>` | Mudanças desde a última sincronização |
| `POST /sync` | Celular envia eventos feitos offline |
| `POST /catalog/android` | Celular envia a lista de apps instalados |
| `POST /heartbeat/extension` | Sinal de vida da extensão |
| `POST /recovery` | Uso da chave de recuperação |
| `GET /health` | Verificação simples de funcionamento |

O contrato da API deve ficar num arquivo OpenAPI no repositório, compartilhado entre os três componentes.

## Fases de desenvolvimento

O projeto é feito em seis fases, cada uma testada pelos critérios de aceite antes de seguir; tudo roda em modo desenvolvimento até a fase 6.

1. **Fundação no Windows:** serviço, banco SQLite, motor de regras, API local e interface básica.
   - Criar uma atividade, agendá-la para dali a 2 min e ver ela começar sozinha
   - O botão Encerrar fica desabilitado e a API recusa o encerramento antes do mínimo
   - Depois do mínimo, encerrar volta tudo para Livre
   - Reiniciar o PC no meio de uma atividade mantém a sessão e o tempo corretos
   - Testes automatizados do motor de regras (estados, fila de conflitos, travas de edição)
2. **Bloqueio de apps no Windows:** agente, encerramento de processos e avisos.
   - Um app não liberado aberto durante a atividade é fechado em até 2 s, com aviso
   - Apps liberados, sempre liberados e processos do sistema nunca são afetados
   - Fora de atividade, nada é bloqueado
3. **Extensão do Chrome**
   - Site não liberado mostra a página de bloqueio; site liberado abre normalmente
   - Abas abertas em sites não liberados são redirecionadas quando a atividade começa
   - Desativar a extensão durante a atividade faz o Chrome ser fechado
4. **App Android com bloqueio de apps**
   - A atividade agendada começa no horário, com a tela desligada
   - App não liberado mostra a tela de bloqueio
   - Reiniciar o celular mantém a sessão
5. **Sincronização**
   - Pareamento por QR code funciona
   - Atividade iniciada no PC aparece no celular em até 60 s na mesma rede, e vice-versa
   - Fora de casa, o celular segue a agenda; ações feitas offline sincronizam ao voltar
6. **Endurecimento e produção:** Device Owner ou Device Admin, políticas do Chrome, chave de recuperação e modo produção.
   - Cada brecha das tabelas de Proteção contra burla é testada uma a uma
   - A chave de recuperação desbloqueia os dois aparelhos

Depois da v1: bloqueio de sites no celular, edição de atividades pelo celular e estatísticas.

## Decisões em aberto

Estas escolhas não bloqueiam o início do desenvolvimento; as primeiras afetam a fase 1, as demais só a fase 6.

- [x] **Conflito de horário:** quando uma atividade agendada chega e a atual ainda está travada, a nova entra na fila (proposta atual) ou substitui a atual? **Decidido: entra na fila.**
- [ ] **Sempre liberados:** além do telefone e do próprio Rotina, o que mais? (WhatsApp, mapas, app do banco, calculadora?)
- [ ] **Ação de bloqueio no Windows:** fechar o app (pode perder trabalho não salvo) ou só cobri-lo com a tela de aviso?
- [ ] **Aviso antecipado:** notificar alguns minutos antes de uma atividade agendada começar?
- [ ] **Android:** Device Owner (proteção forte, exige remover as contas temporariamente) ou Device Admin (mais simples, mais fraco)?
- [ ] **Conta no Windows:** passar a usar uma conta padrão no dia a dia, com a senha de administrador guardada com outra pessoa?
- [ ] **Chave de recuperação:** quem guarda, e por quanto tempo ela libera os aparelhos?
- [ ] **Extensão:** publicar como não listada na Chrome Web Store para permitir a instalação forçada, ou contar só com o heartbeat?

## Ajustes decididos (19/10/2026)

Estes ajustes valem por cima do texto acima quando houver diferença.

**Arquitetura**

- No PC são 2 programas, não 3: o Rotina Service e o Rotina App (WPF). O App junta a interface de edição e o papel do agente (ícone na bandeja e avisos de bloqueio). Quem detecta e fecha os apps é o App, não o serviço: o serviço roda na sessão 0 e não enxerga as janelas do usuário. O serviço reabre o App sempre que ele for fechado durante uma atividade.
- Interface em WPF, não WinUI 3.
- Sem mDNS na v1: o celular usa o IP e a porta que vêm no QR code. Reserve o IP do PC no roteador.
- .NET 10 (LTS).

**Bloqueio no Windows**

- Ferramentas do sistema que ficam em `C:\Windows` (Gerenciador de Tarefas, `cmd.exe`, PowerShell, Windows Terminal, Editor do Registro, `mmc.exe`, Configurações, Painel de Controle) são bloqueadas por padrão, apesar da regra geral de não tocar em `C:\Windows`. Elas podem ser liberadas numa atividade ou na lista de sempre liberados, como qualquer app.
- Só são encerrados processos que têm janela visível na sessão do usuário. Processos em segundo plano (drivers, bandeja, atualizadores) nunca são encerrados.

**Agenda e sessões**

- Horários ficam em UTC. A agenda é interpretada num fuso horário fixo gravado nas configurações na primeira execução, e não no fuso atual do sistema, para que mudar o fuso não desloque a agenda.
- A sessão de uma atividade agendada tem id determinístico, igual nos dois aparelhos: os 16 primeiros bytes do SHA-256 do texto `schedule:{scheduleEntryId}:{data local yyyy-MM-dd}`, escritos como UUID. Ela começa no horário agendado (`startedAt` = horário da ocorrência), mesmo que o aparelho tenha ligado depois.
- Se o aparelho estava desligado no horário, a ocorrência perdida só começa ao ligar se o tempo mínimo dela ainda não tiver passado.
- Fila: se chega mais de uma atividade agendada enquanto a atual está travada, vale a mais recente. Ela começa exatamente quando o mínimo da atual é atingido.
- Uma ocorrência anterior à última alteração da linha da agenda é ignorada, para que criar ou editar um horário que já passou não inicie a atividade na hora.

**Extensão do Chrome**

- O serviço só fecha o Chrome depois de uns 60 s sem heartbeat e só se o Chrome estiver rodando, porque o MV3 adormece a extensão.
- As regras de bloqueio valem só para `main_frame` e `sub_frame`, para que sites liberados carreguem imagens e scripts de outros domínios.

**App Android**

- A cópia local fica num arquivo JSON com o mesmo formato da API do PC, não no Room: o volume é pequeno e a sincronização só precisa salvar o que a API devolve.
- O motor de regras é uma cópia fiel do C# (`Engine.kt`), e um teste com o mesmo vetor nos dois lados garante o mesmo id de sessão agendada.
- O serviço em primeiro plano só é iniciado pelo alarme exato, pelo boot ou com o app aberto, porque o Android 12+ proíbe iniciá-lo em segundo plano. O bloqueio pela acessibilidade funciona mesmo sem ele.
- O app Configurações também se declara "tela inicial" (FallbackHome) e por isso não entra na lista de launchers isentos de bloqueio.
- Em desenvolvimento o celular fala com o PC por `adb reverse tcp:47800 tcp:47800` (emulador ou cabo USB), no endereço `127.0.0.1`.

**Sincronização**

- O serviço escuta em todas as interfaces na porta 47800. Pedidos do próprio PC não usam token, mas o Host precisa ser `localhost` ou `127.0.0.1` (proteção contra DNS rebinding). Pedidos da rede exigem `Authorization: Bearer <token>`; `/pairing` nunca é alcançável pela rede.
- Um celular por vez. Gerar um código novo troca o token, e o antigo deixa de valer. O PC guarda só o hash SHA-256 do token.
- `GET /sync?since=<rev>` devolve atividades, agenda, sempre liberados e sessões com revisão maior. `POST /sync` recebe as sessões que o celular criou ou encerrou e aplica cada uma pela regra `Engine.MergeSession`: nada encerra antes do mínimo e, se duas atividades se sobrepõem, a mais nova só vale se a anterior já podia ser encerrada. O PC vence os empates.
- O celular sincroniza a cada ~45 s (pelo serviço de acessibilidade), quando a rede volta e logo depois de iniciar ou encerrar. As mudanças feitas offline ficam numa fila até o PC aceitar.
- O celular envia ao PC a lista de apps com ícone, para escolher os apps Android liberados numa lista com busca.

## Como entregar ao Claude Code

Exporte este documento como Markdown, salve como `docs/SPEC.md` numa pasta nova de projeto e peça ao Claude Code para trabalhar uma fase por vez.

**Estrutura sugerida do repositório**

```
rotina/
  docs/SPEC.md            ← este documento
  docs/api.yaml           ← contrato OpenAPI
  windows/                ← solução .NET: Core, Service, Agent, UI, Tests
  android/                ← projeto Android Studio
  chrome-extension/       ← extensão Manifest V3
  CLAUDE.md               ← convenções que o Claude Code cria e segue
```

**Instale antes no PC:** Git, SDK do .NET, Android Studio (com SDK e emulador), Chrome, e ative a depuração USB no celular.

**Primeira mensagem sugerida para o Claude Code**

```
Leia docs/SPEC.md. Antes de escrever código, revise a especificação,
aponte riscos técnicos e me pergunte o que for necessário das Decisões
em aberto. Depois crie o CLAUDE.md com as convenções do projeto,
inicialize o git e implemente só a Fase 1, com testes do motor de
regras. Pare ao fim da fase para eu testar os critérios de aceite.
```

**Cuidados**

- Ligue o modo produção só no fim, depois de gerar e guardar a chave de recuperação. Antes disso, um bug pode travar o celular ou o PC
- Teste o Android primeiro no emulador; no celular de verdade, só depois que o bloqueio estiver estável
- Faça commit ao fim de cada fase, para poder voltar atrás
