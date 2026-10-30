using System;
using System.Text.Json;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using IFAI.Services;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.WinForms;

namespace IFAI
{
    public partial class Form1 : Form
    {
        private readonly DatabaseService dbService;
        private readonly EvasaoMLService mlService;
        private WebView2 webView;

        public Form1()
        {
            InitializeComponent();

            // 1. Instancia o controle na tela
            webView = new WebView2 { Dock = DockStyle.Fill };
            this.Controls.Add(webView);

            // Injeção manual de dependências
            dbService = new DatabaseService();
            mlService = new EvasaoMLService();
            dbService.PopularBancoInicial();
            this.Text = "IFAI - Controle de Evasão Escolar";

            // Descomente a linha abaixo na primeira execução para treinar o modelo
            // mlService.TreinarModelo(System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "dados_historicos.csv"));
        }

        //ADICIONE ESTE BLOCO: O OnLoad garante que a janela já existe no Windows
        protected override async void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            await InitializeWebViewAsync();
        }

        private async Task InitializeWebViewAsync()
        {
            await webView.EnsureCoreWebView2Async(null);

            string appPath = Path.Combine(Application.StartupPath, "dist");

            // Como a inicialização deu certo, CoreWebView2 não é mais nulo
            webView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                "ifai.app",
                appPath,
                CoreWebView2HostResourceAccessKind.Allow
            );

            webView.CoreWebView2.WebMessageReceived += CoreWebView2_WebMessageReceived;
            webView.Source = new Uri("https://ifai.app/index.html");
        }

        // ====================================================================
        // INTERCEPTADOR DE MENSAGENS DO REACT
        // ====================================================================
        // 1. ATUALIZE SUAS CLASSES DTO (Para incluir a propriedade Turma)
        private class ReactMessage
        {
            public string Action { get; set; }
            public PayloadData Payload { get; set; }
        }

        private class PayloadData
        {
            public string Turma { get; set; } // Adicionado para sabermos qual turma buscar
            public int AlunoId { get; set; }
            public int Bimestre { get; set; }
            public float Frequencia { get; set; }
            public float Nota { get; set; }
            public int Faltas { get; set; }
            public int Advertencias { get; set; }
        }

        // 2. ATUALIZE O OUVINTE DE MENSAGENS
        private void CoreWebView2_WebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                string jsonMessage = e.TryGetWebMessageAsString();
                var message = JsonSerializer.Deserialize<ReactMessage>(jsonMessage, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                // === AÇÃO 1: REACT PEDINDO OS DADOS REAIS DE UMA TURMA ===
                // Dentro de CoreWebView2_WebMessageReceived, no bloco CARREGAR_TURMA
                if (message?.Action == "CARREGAR_TURMA")
                {
                    var painelCompleto = dbService.ObterPainelCompleto(message.Payload.Turma);

                    var respostaJson = JsonSerializer.Serialize(new
                    {
                        Action = "PAINEL_COMPLETO",
                        Turma = message.Payload.Turma,
                        Dados = painelCompleto,
                        ImportanciaIA = mlService.PesosDoModelo, // <-- NOVA LINHA ADICIONADA AQUI
                        MetricasIA = mlService.MetricasDoModelo
                    });

                    webView.CoreWebView2.PostWebMessageAsString(respostaJson);
                }

                // === AÇÃO 2: GESTOR SALVANDO NOTAS DE UM ALUNO ESPECÍFICO ===
                else if (message?.Action == "ANALISE_NOVO_BIMESTRE")
                {
                    var payload = message.Payload;

                    // 1. Salva/Atualiza o histórico
                    dbService.InserirHistorico(payload.AlunoId, payload.Bimestre, payload.Frequencia, payload.Nota, payload.Faltas, payload.Advertencias);

                    // 2. Busca as 13 variáveis constantes do Banco de Dados
                    string variaveisEstaticas = dbService.ObterVariaveisEstaticas(payload.AlunoId);
                    string[] estaticasArray = string.IsNullOrEmpty(variaveisEstaticas) ? new string[13] : variaveisEstaticas.Split(',');

                    // Função rápida para evitar erros se a string do banco estiver vazia ou menor
                    float ParseStatic(int index) => estaticasArray.Length > index && float.TryParse(estaticasArray[index], System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out float v) ? v : 0f;

                    // 3. Monta o objeto explícito para o ML.NET
                    var dadosAnalise = new StudentData
                    {
                        // Dinâmicas
                        Nota = payload.Nota,
                        Frequencia = payload.Frequencia,

                        // A MÁGICA AQUI: O C# sobrepõe os dados do React e calcula as faltas/advertências 
                        // matematicamente igual ao Python, destravando a inteligência da IA!
                        Faltas = (float)Math.Max(0, Math.Round((100 - payload.Frequencia) / 5)),
                        Advertencias = payload.Nota < 5.0f ? 1 : 0,

                        // Estáticas (Vêm do SQLite)
                        MaritalStatus = ParseStatic(0),
                        DaytimeEvening = ParseStatic(1),
                        Nationality = ParseStatic(2),
                        MotherQual = ParseStatic(3),
                        FatherQual = ParseStatic(4),
                        MotherOcc = ParseStatic(5),
                        FatherOcc = ParseStatic(6),
                        SpecialNeeds = ParseStatic(7),
                        Gender = ParseStatic(8),
                        Age = ParseStatic(9),
                        UnemploymentRate = ParseStatic(10),
                        InflationRate = ParseStatic(11),
                        GDP = ParseStatic(12)
                    };

                    // 4. Calcula o risco
                    int riscoCalculado = mlService.PreverRisco(dadosAnalise);

                    dbService.AtualizarRiscoAluno(payload.AlunoId, riscoCalculado);

                    var respostaJson = JsonSerializer.Serialize(new
                    {
                        Action = "RESULTADO_ANALISE",
                        AlunoId = payload.AlunoId
                    });

                    webView.CoreWebView2.PostWebMessageAsString(respostaJson);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Erro interno:\n{ex.Message}", "Erro", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }
}