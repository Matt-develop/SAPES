using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Text.Json;
using Microsoft.ML;
using Microsoft.ML.Data;
using Microsoft.ML.Trainers;
using Microsoft.ML.Calibrators;

namespace IFAI.Services
{
    public class StudentData
    {
        [LoadColumn(0)] public float Nota { get; set; }
        [LoadColumn(1)] public float Frequencia { get; set; }
        [LoadColumn(2)] public float Faltas { get; set; }
        [LoadColumn(3)] public float Advertencias { get; set; }
        [LoadColumn(4)] public float MaritalStatus { get; set; }
        [LoadColumn(5)] public float DaytimeEvening { get; set; }
        [LoadColumn(6)] public float Nationality { get; set; }
        [LoadColumn(7)] public float MotherQual { get; set; }
        [LoadColumn(8)] public float FatherQual { get; set; }
        [LoadColumn(9)] public float MotherOcc { get; set; }
        [LoadColumn(10)] public float FatherOcc { get; set; }
        [LoadColumn(11)] public float SpecialNeeds { get; set; }
        [LoadColumn(12)] public float Gender { get; set; }
        [LoadColumn(13)] public float Age { get; set; }
        [LoadColumn(14)] public float UnemploymentRate { get; set; }
        [LoadColumn(15)] public float InflationRate { get; set; }
        [LoadColumn(16)] public float GDP { get; set; }
        [LoadColumn(17), ColumnName("Label")] public bool Evasao { get; set; }
    }

    public class EvasaoPrediction
    {
        [ColumnName("Probability")] public float Probability { get; set; }
    }

    public class EvasaoMLService
    {
        private readonly MLContext mlContext;
        private ITransformer modeloTreinado;
        private PredictionEngine<StudentData, EvasaoPrediction> predictionEngine;
        private readonly string modelPath;
        private readonly string pesosPath;
        // Adicione esta propriedade no topo da classe EvasaoMLService, logo abaixo da PesosDoModelo:
        public object MetricasDoModelo { get; private set; }
        private readonly string metricasPath;

        // Esta lista vai guardar os maiores fatores de evasão para mandar ao React
        public List<object> PesosDoModelo { get; private set; } = new List<object>();

        // Modifique o construtor para inicializar o caminho das métricas:
        public EvasaoMLService()
        {
            mlContext = new MLContext(seed: 0);
            modelPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ModeloEvasao.zip");
            pesosPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "pesos_modelo.json");
            metricasPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "metricas_modelo.json"); // NOVA LINHA
            LoadModel();
        }

        public void TreinarModelo(string dataPath)
        {
            if (!File.Exists(dataPath)) return;

            IDataView baseCompleta = mlContext.Data.LoadFromTextFile<StudentData>(path: dataPath, hasHeader: true, separatorChar: ',');

            // 1. DIVISÃO ACADÊMICA: 80% para Treino, 20% para Teste (Garante validade científica para o TCC)
            var split = mlContext.Data.TrainTestSplit(baseCompleta, testFraction: 0.2, seed: 42);
            var dadosTreino = split.TrainSet;
            var dadosTeste = split.TestSet;

            var pipeline = mlContext.Transforms.Categorical.OneHotEncoding(new[]
                {
            new InputOutputColumnPair("MaritalStatusEncoded", nameof(StudentData.MaritalStatus)),
            new InputOutputColumnPair("NationalityEncoded", nameof(StudentData.Nationality)),
            new InputOutputColumnPair("MotherQualEncoded", nameof(StudentData.MotherQual)),
            new InputOutputColumnPair("FatherQualEncoded", nameof(StudentData.FatherQual)),
            new InputOutputColumnPair("MotherOccEncoded", nameof(StudentData.MotherOcc)),
            new InputOutputColumnPair("FatherOccEncoded", nameof(StudentData.FatherOcc))
        })
                .Append(mlContext.Transforms.NormalizeMinMax(nameof(StudentData.Age)))
                .Append(mlContext.Transforms.NormalizeMinMax(nameof(StudentData.Nota)))
                .Append(mlContext.Transforms.NormalizeMinMax(nameof(StudentData.Frequencia)))
                .Append(mlContext.Transforms.Concatenate("Features",
                    "MaritalStatusEncoded", "NationalityEncoded", "MotherQualEncoded",
                    "FatherQualEncoded", "MotherOccEncoded", "FatherOccEncoded",
                    nameof(StudentData.DaytimeEvening), nameof(StudentData.SpecialNeeds),
                    nameof(StudentData.Gender), nameof(StudentData.Age),
                    nameof(StudentData.UnemploymentRate), nameof(StudentData.InflationRate),
                    nameof(StudentData.GDP), nameof(StudentData.Nota),
                    nameof(StudentData.Frequencia), nameof(StudentData.Faltas),
                    nameof(StudentData.Advertencias)))
                .Append(mlContext.BinaryClassification.Trainers.SdcaLogisticRegression(labelColumnName: "Label", featureColumnName: "Features"));

            modeloTreinado = pipeline.Fit(dadosTreino);
            mlContext.Model.Save(modeloTreinado, dadosTreino.Schema, modelPath);

            // 2. AVALIAÇÃO NATIVA DO ML.NET
            var previsoesteste = modeloTreinado.Transform(dadosTeste);
            var metricas = mlContext.BinaryClassification.Evaluate(previsoesteste, labelColumnName: "Label");

            // Extrai a Matriz de Confusão 2x2
            var matriz = metricas.ConfusionMatrix;
            int tn = (int)matriz.Counts[0][0]; // Verdadeiro Negativo (Previu Graduação, Era Graduação)
            int fp = (int)matriz.Counts[0][1]; // Falso Positivo (Previu Evasão, Era Graduação)
            int fn = (int)matriz.Counts[1][0]; // Falso Negativo (Previu Graduação, Era Evasão)
            int tp = (int)matriz.Counts[1][1]; // Verdadeiro Positivo (Previu Evasão, Era Evasão)

            MetricasDoModelo = new
            {
                Acuracia = Math.Round(metricas.Accuracy * 100, 2),
                F1Score = Math.Round(metricas.F1Score * 100, 2),
                AUC = Math.Round(metricas.AreaUnderRocCurve * 100, 2), // Área sob a curva ROC
                Matriz = new { VerdadeiroPositivo = tp, VerdadeiroNegativo = tn, FalsoPositivo = fp, FalsoNegativo = fn }
            };

            File.WriteAllText(metricasPath, JsonSerializer.Serialize(MetricasDoModelo));

            ExtrairESalvarPesos(baseCompleta);
            LoadModel();
        }

        private void ExtrairESalvarPesos(IDataView trainingData)
        {
            try
            {
                var transformedData = modeloTreinado.Transform(trainingData);

                // CORREÇÃO CRÍTICA: ML.NET esconde o tipo exato do TransformerChain. 
                // A forma segura de ler é tratar como uma lista de transformadores (IEnumerable).
                var transformers = modeloTreinado as IEnumerable<ITransformer>;
                var lastTransformer = transformers?.LastOrDefault();

                if (lastTransformer == null)
                {
                    File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "erro_xai.txt"), "Falha ao obter o último transformador do pipeline.");
                    return;
                }

                float[] weightsArray = null;

                // Tentativa 1: Modelo Linear Puro
                if (lastTransformer is ISingleFeaturePredictionTransformer<LinearBinaryModelParameters> puro)
                {
                    weightsArray = puro.Model.Weights.ToArray();
                }
                // Tentativa 2: Modelo Calibrado (Comum no SdcaLogisticRegression)
                else if (lastTransformer is ISingleFeaturePredictionTransformer<CalibratedModelParametersBase<LinearBinaryModelParameters, PlattCalibrator>> calibrado)
                {
                    weightsArray = calibrado.Model.SubModel.Weights.ToArray();
                }
                // Tentativa 3: Força Bruta Segura (Reflection com proteções null)
                else
                {
                    var propModel = lastTransformer.GetType().GetProperty("Model");
                    if (propModel != null)
                    {
                        var modelObj = propModel.GetValue(lastTransformer);
                        if (modelObj != null)
                        {
                            var propSubModel = modelObj.GetType().GetProperty("SubModel");
                            if (propSubModel != null)
                            {
                                modelObj = propSubModel.GetValue(modelObj) ?? modelObj;
                            }

                            var propWeights = modelObj.GetType().GetProperty("Weights");
                            if (propWeights != null)
                            {
                                var vBuffer = propWeights.GetValue(modelObj);
                                var toArrayMethod = vBuffer?.GetType().GetMethod("ToArray");
                                if (toArrayMethod != null)
                                {
                                    weightsArray = (float[])toArrayMethod.Invoke(vBuffer, null);
                                }
                            }
                        }
                    }
                }

                if (weightsArray != null)
                {
                    VBuffer<ReadOnlyMemory<char>> slotNames = default;
                    transformedData.Schema["Features"].GetSlotNames(ref slotNames);
                    var names = slotNames.GetValues().ToArray();

                    var importancias = new Dictionary<string, float>();

                    for (int i = 0; i < names.Length && i < weightsArray.Length; i++)
                    {
                        string nomeCru = names[i].ToString().Split('_')[0];
                        string nomeAmigavel = TraduzirNome(nomeCru);

                        if (!importancias.ContainsKey(nomeAmigavel)) importancias[nomeAmigavel] = 0;
                        importancias[nomeAmigavel] += Math.Abs(weightsArray[i]);
                    }

                    // Pega os 7 maiores fatores que impactam a evasão
                    PesosDoModelo = importancias
                        .OrderByDescending(x => x.Value)
                        .Take(7)
                        .Select(x => (object)new { name = x.Key, weight = Math.Round(x.Value, 2) })
                        .ToList();

                    File.WriteAllText(pesosPath, JsonSerializer.Serialize(PesosDoModelo));
                }
                else
                {
                    File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "erro_xai.txt"),
                        "Pesos não encontrados. Tipo do modelo: " + lastTransformer.GetType().Name);
                }
            }
            catch (Exception ex)
            {
                File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "erro_xai.txt"),
                    "Exceção XAI: " + ex.Message + "\n" + ex.StackTrace);
            }
        }

        private string TraduzirNome(string nomeIngles)
        {
            return nomeIngles switch
            {
                "Faltas" => "Ausências",
                "Advertencias" => "Advertências Disciplinares",
                "Nota" => "Desempenho Escolar (Nota)",
                "Frequencia" => "Assiduidade",
                "Age" => "Defasagem Idade-Série",
                "UnemploymentRate" => "Taxa de Desemprego (Macro)",
                "GDP" => "PIB da Região",
                "MotherQualEncoded" => "Escolaridade da Mãe",
                "FatherQualEncoded" => "Escolaridade do Pai",
                "MotherOccEncoded" => "Profissão da Mãe",
                "FatherOccEncoded" => "Profissão do Pai",
                "MaritalStatusEncoded" => "Estado Civil dos Pais",
                "NationalityEncoded" => "Nacionalidade",
                "Gender" => "Gênero",
                "SpecialNeeds" => "Necessidades Especiais",
                "DaytimeEvening" => "Turno (Diurno/Noturno)",
                _ => nomeIngles
            };
        }

        private void LoadModel()
        {
            if (File.Exists(modelPath))
            {
                modeloTreinado = mlContext.Model.Load(modelPath, out var _);
                predictionEngine = mlContext.Model.CreatePredictionEngine<StudentData, EvasaoPrediction>(modeloTreinado);
            }
            if (File.Exists(pesosPath))
            {
                var json = File.ReadAllText(pesosPath);
                PesosDoModelo = JsonSerializer.Deserialize<List<object>>(json);
            }
            if (File.Exists(metricasPath)) { MetricasDoModelo = JsonSerializer.Deserialize<object>(File.ReadAllText(metricasPath)); }
        }

        public int PreverRisco(StudentData dados)
        {
            if (predictionEngine == null) return 0;
            return (int)(predictionEngine.Predict(dados).Probability * 100);
        }
    }
}