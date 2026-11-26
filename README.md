# Rotina

App pessoal que organiza o dia em atividades e, durante cada atividade, só libera os apps e sites escolhidos para ela. Funciona no PC Windows e no celular Android, sincronizados pela rede de casa. Não tem conta, nuvem nem multiusuário.

## Como funciona

- Cada **atividade** tem nome, cor, tempo mínimo e listas de liberados: apps do Windows, apps do Android e sites. Durante a atividade, tudo o que não está na lista fica bloqueado.
- Uma atividade começa sozinha pela **agenda semanal** ou é iniciada à mão, pelo PC ou pelo celular.
- Antes do tempo mínimo, a atividade fica **travada**: não dá para encerrar nem trocar. Depois do mínimo, o botão Encerrar libera.
- Se uma atividade agendada chega com a atual ainda travada, ela entra na fila e começa quando o mínimo acabar. O botão **Antecipar** começa a próxima agendada agora.
- Avisos 5 minutos e 1 minuto antes de cada início agendado.
- Uma **chave de recuperação** encerra a atividade antes do mínimo, para emergências. Ela também funciona no celular sem conexão.
- **Modo desenvolvimento** (padrão): o tempo mínimo conta em segundos, para testar. O **modo produção** é ligado pela interface do PC.

## Componentes

| Pasta | O que é | Tecnologia |
|---|---|---|
| `windows/Rotina.Core` | Modelos e motor de regras (`Engine`), sem I/O | .NET 10 |
| `windows/Rotina.Service` | Serviço do Windows: API local, banco SQLite, ciclo de 1 s | ASP.NET Core (Kestrel) |
| `windows/Rotina.App` | Interface de edição, ícone na bandeja, avisos e bloqueio de apps | WPF |
| `windows/Rotina.Tests` | Testes do motor de regras | xUnit |
| `chrome-extension` | Bloqueia no Chrome os sites não liberados | Manifest V3 |
| `android` | App do celular: agenda, bloqueio de apps, sincronização | Kotlin + Jetpack Compose |
| `docs` | Especificação (`SPEC.md`), contrato da API (`api.yaml`) e ícones | |

O serviço do PC é a fonte da verdade. Ele escuta em `127.0.0.1:47800` (e na rede privada, para o celular) e guarda os dados em `%ProgramData%\Rotina\rotina.db`. A interface e a extensão só falam com o serviço. O celular pareia com o PC por QR code e guarda uma cópia local, para seguir a agenda fora de casa.

## Requisitos

- Windows 10 ou 11 com o [.NET 10 SDK](https://dotnet.microsoft.com/download)
- Google Chrome
- Android 10 (API 29) ou mais novo
- Para compilar o app Android: Android Studio (JDK do próprio Android Studio)

## Instalação

### PC (serviço + interface)

Abra o PowerShell como administrador na pasta do projeto:

```powershell
.\windows\install-service.ps1
```

O script:

- publica o serviço e a interface em `C:\Program Files\Rotina`;
- registra o serviço `Rotina` com início automático e reinício em caso de falha;
- abre a interface na bandeja a cada login;
- libera a porta 47800 no firewall só em redes privadas;
- restringe a pasta de dados ao sistema e aos administradores;
- grava políticas do Chrome que desligam janela anônima, modo visitante e novos perfis.

Para remover: `.\windows\install-service.ps1 -Uninstall`. Os dados continuam em `%ProgramData%\Rotina`.

### Extensão do Chrome

1. Abra `chrome://extensions` e ative o **Modo do desenvolvedor**.
2. Clique em **Carregar sem compactação** e escolha a pasta `chrome-extension`.

### Celular

1. Compile o APK (veja abaixo) e instale no celular.
2. No Android 13 ou mais novo, antes de ativar a acessibilidade: Informações do app, menu ⋮, **Permitir configurações restritas**.
3. Abra o app, siga o checklist de permissões na aba Configuração e leia o QR code de pareamento mostrado na interface do PC.

## Como rodar em desenvolvimento

Atenção: durante uma atividade, a interface fecha toda janela fora da lista, inclusive editor, terminal e navegador. Libere o que estiver usando antes de testar.

```powershell
dotnet run --project windows/Rotina.Service
dotnet run --project windows/Rotina.App
```

Para testar com um banco descartável, defina `ROTINA_DATA_DIR` com outra pasta antes de rodar o serviço.

App Android (com `JAVA_HOME` apontando para o JDK do Android Studio):

```powershell
cd android
.\gradlew.bat :app:assembleDebug
```

O APK sai em `android/app/build/outputs/apk/debug/`. No emulador, rode `adb reverse tcp:47800 tcp:47800` e use "Importar do PC" na aba Configuração.

## Testes

```powershell
dotnet test windows/Rotina.Tests
node --test chrome-extension/rules.test.mjs
cd android; .\gradlew.bat :app:testDebugUnitTest
```

O `Engine.kt` do Android é cópia do `Engine.cs` do Windows. Qualquer mudança de regra vai nos dois, e os testes dos dois lados precisam passar.

## Limitações conhecidas

- O token do celular viaja em HTTP sem criptografia na rede de casa.
- Se o serviço do PC parar de vez, a extensão do Chrome mantém o bloqueio de sites.
- Bloqueio de sites no celular ainda não existe.
