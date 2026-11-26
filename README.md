# NVR Video LAN — Linux

Gravador de vídeo em rede experimental, versão Linux 0.6.5, desenvolvido por **[Talison Roberto Maurer](https://github.com/talisonmaurer)**.

Interface web, gravação RTSP em segmentos MP4 com FFmpeg, descoberta ONVIF, PTZ em câmeras compatíveis, mosaico personalizável, armazenamento em múltiplos discos e monitor local em tela cheia.

## Requisitos e instalação

Linux x86-64 com systemd, SDK .NET 10, FFmpeg instalado pelo gerenciador de pacotes e dependências nativas do .NET. Este pacote público ainda não foi validado em uma instalação limpa.

```bash
git clone https://github.com/talisonmaurer/NVR-Video-Lan-Linux.git
cd NVR-Video-Lan-Linux
dotnet publish NvrVideoLan.csproj -c Release -r linux-x64 --self-contained true -p:NvrEmbedFfmpeg=false -o ./build/publish
sudo useradd --system --user-group --no-create-home --shell /usr/sbin/nologin nvr-video-lan
sudo install -d -m 755 /opt/nvr-video-lan
sudo cp -a build/publish/. /opt/nvr-video-lan/
sudo chown -R root:root /opt/nvr-video-lan
sudo touch /opt/nvr-video-lan/installed.marker
sudo install -m 644 linux/nvr-video-lan.service /etc/systemd/system/nvr-video-lan.service
sudo systemctl daemon-reload
sudo systemctl enable --now nvr-video-lan
```

Instale FFmpeg antes de iniciar. Se a conta já existir, não a recrie. Abra http://127.0.0.1:5088 no servidor, ou use um túnel SSH: `ssh -L 5088:127.0.0.1:5088 usuario@servidor`. Configure uma senha forte no primeiro acesso. A configuração padrão é restrita ao loopback. Para outros computadores, configure autenticação, proxy HTTPS confiável e restrições de firewall. Não exponha a aplicação diretamente à internet.

Monte discos de gravação de forma persistente, crie diretórios exclusivos do NVR com permissão de escrita para a conta do serviço e configure volumes, cotas e retenção pela interface. Teste gravação, reprodução, recuperação de falhas e retenção antes de uso crítico. A limpeza exclui gravações antigas; utilize discos dedicados. Não use como único sistema de segurança sem validação.

Logs: `journalctl -u nvr-video-lan`. O monitor local é acessível em `http://127.0.0.1:5088/monitor` e configurado pela interface administrativa. Usuários locais podem acessar esse endpoint; não o exponha via proxy. Um navegador em modo kiosk pode ser iniciado pela sessão gráfica, separadamente do serviço.

## Autoria e licenças

Autor: Talison Roberto Maurer. Código disponibilizado para consulta pública, sem licença de redistribuição definida para o projeto até o momento. A biblioteca hls.js incluída mantém sua licença Apache-2.0 e os avisos dos autores originais. FFmpeg não está incorporado nesta distribuição Linux; use a versão do sistema e observe sua licença. Nenhum instalador Windows ou binário de FFmpeg é distribuído aqui.
