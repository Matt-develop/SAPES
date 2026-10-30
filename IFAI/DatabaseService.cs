using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;

namespace IFAI.Services
{
    public class DatabaseService
    {
        private readonly string connectionString;

        public DatabaseService()
        {
            string dbPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "escola.db");
            connectionString = $"Data Source={dbPath}";
            InitializeDatabase();
        }

        private void InitializeDatabase()
        {
            using var connection = new SqliteConnection(connectionString);
            connection.Open();

            // Adicionamos a coluna VariaveisEstaticas. 
            // Nela o gestor vai colar as 281 características do CSV separadas por vírgula usando o DB Browser.
            string createAlunosTable = @"
                CREATE TABLE IF NOT EXISTS Alunos (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    Nome TEXT NOT NULL,
                    Turma TEXT NOT NULL,
                    RiscoAtual INTEGER DEFAULT 0,
                    VariaveisEstaticas TEXT DEFAULT ''
                );";

            string createHistoricoTable = @"
                CREATE TABLE IF NOT EXISTS Historico (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    AlunoId INTEGER NOT NULL,
                    Bimestre INTEGER NOT NULL,
                    Frequencia REAL NOT NULL,
                    Nota REAL NOT NULL,
                    Faltas INTEGER NOT NULL,
                    Advertencias INTEGER NOT NULL,
                    FOREIGN KEY(AlunoId) REFERENCES Alunos(Id)
                );";

            new SqliteCommand(createAlunosTable, connection).ExecuteNonQuery();
            new SqliteCommand(createHistoricoTable, connection).ExecuteNonQuery();
        }

        public void PopularBancoInicial()
        {
            using var connection = new SqliteConnection(connectionString);
            connection.Open();
            long count = (long)new SqliteCommand("SELECT COUNT(*) FROM Alunos", connection).ExecuteScalar();

            if (count == 0)
            {
                // Mude esta linha dentro de PopularBancoInicial():
                string mockStaticVars = string.Join(",", new string[13].Select(_ => "0"));

                string insertAlunos = $@"
                    INSERT INTO Alunos (Nome, Turma, RiscoAtual, VariaveisEstaticas) VALUES ('Tiago Martins', '9º A', 85, '{mockStaticVars}');
                    INSERT INTO Alunos (Nome, Turma, RiscoAtual, VariaveisEstaticas) VALUES ('Ana Maria', '9º A', 45, '{mockStaticVars}');
                ";
                new SqliteCommand(insertAlunos, connection).ExecuteNonQuery();

                string insertHistorico = @"
                    INSERT INTO Historico (AlunoId, Bimestre, Frequencia, Nota, Faltas, Advertencias) VALUES (1, 1, 45.0, 5.0, 12, 3);
                    INSERT INTO Historico (AlunoId, Bimestre, Frequencia, Nota, Faltas, Advertencias) VALUES (2, 1, 60.0, 5.7, 8, 1);
                ";
                new SqliteCommand(insertHistorico, connection).ExecuteNonQuery();
            }
        }

        // Método para o Form1.cs puxar as variáveis estáticas do aluno
        public string ObterVariaveisEstaticas(int alunoId)
        {
            using var connection = new SqliteConnection(connectionString);
            connection.Open();
            using var cmd = new SqliteCommand("SELECT VariaveisEstaticas FROM Alunos WHERE Id = $id", connection);
            cmd.Parameters.AddWithValue("$id", alunoId);
            var result = cmd.ExecuteScalar();
            return result != null ? result.ToString() : "";
        }

        public void InserirHistorico(int alunoId, int bimestre, float frequencia, float nota, int faltas, int advertencias)
        {
            using var connection = new SqliteConnection(connectionString);
            connection.Open();
            // Verifica se o bimestre já existe para sobrescrever, evitando duplicações
            string checkCmd = "SELECT COUNT(*) FROM Historico WHERE AlunoId = $alunoId AND Bimestre = $bimestre";
            using var check = new SqliteCommand(checkCmd, connection);
            check.Parameters.AddWithValue("$alunoId", alunoId);
            check.Parameters.AddWithValue("$bimestre", bimestre);
            long exists = (long)check.ExecuteScalar();

            string query = exists > 0
                ? "UPDATE Historico SET Frequencia=$frequencia, Nota=$nota, Faltas=$faltas, Advertencias=$advertencias WHERE AlunoId=$alunoId AND Bimestre=$bimestre"
                : "INSERT INTO Historico (AlunoId, Bimestre, Frequencia, Nota, Faltas, Advertencias) VALUES ($alunoId, $bimestre, $frequencia, $nota, $faltas, $advertencias)";

            using var command = new SqliteCommand(query, connection);
            command.Parameters.AddWithValue("$alunoId", alunoId);
            command.Parameters.AddWithValue("$bimestre", bimestre);
            command.Parameters.AddWithValue("$frequencia", frequencia);
            command.Parameters.AddWithValue("$nota", nota);
            command.Parameters.AddWithValue("$faltas", faltas);
            command.Parameters.AddWithValue("$advertencias", advertencias);
            command.ExecuteNonQuery();
        }

        public void AtualizarRiscoAluno(int alunoId, int novoRisco)
        {
            using var connection = new SqliteConnection(connectionString);
            connection.Open();
            using var command = new SqliteCommand("UPDATE Alunos SET RiscoAtual = $risco WHERE Id = $id", connection);
            command.Parameters.AddWithValue("$risco", novoRisco);
            command.Parameters.AddWithValue("$id", alunoId);
            command.ExecuteNonQuery();
        }

        public object ObterPainelCompleto(string turma)
        {
            using var connection = new SqliteConnection(connectionString);
            connection.Open();

            var alunos = new List<object>();
            using (var cmd = new SqliteCommand(@"SELECT a.Id, a.Nome, 
                       COALESCE((SELECT Frequencia FROM Historico h WHERE h.AlunoId = a.Id ORDER BY Bimestre DESC LIMIT 1), 0) as Frequencia,
                       COALESCE((SELECT Nota FROM Historico h WHERE h.AlunoId = a.Id ORDER BY Bimestre DESC LIMIT 1), 0) as Nota,
                       a.RiscoAtual FROM Alunos a WHERE a.Turma = $turma", connection))
            {
                cmd.Parameters.AddWithValue("$turma", turma);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    alunos.Add(new
                    {
                        id = reader.GetInt32(0),
                        name = reader.GetString(1),
                        frequency = reader.GetFloat(2).ToString("0.0") + "%",
                        grade = reader.GetFloat(3).ToString("0.0"),
                        risk = reader.GetInt32(4)
                    });
                }
            }

            var topRisco = new List<object>();
            using (var cmd = new SqliteCommand("SELECT Nome, RiscoAtual FROM Alunos WHERE Turma = $turma ORDER BY RiscoAtual DESC LIMIT 3", connection))
            {
                cmd.Parameters.AddWithValue("$turma", turma);
                using var reader = cmd.ExecuteReader();
                while (reader.Read()) { topRisco.Add(new { name = reader.GetString(0), probability = reader.GetInt32(1) }); }
            }

            // === GRÁFICO DE LINHA DINÂMICO === (Calcula a média de frequência de toda a turma por Bimestre)
            var frequenciaMensal = new List<object>();
            using (var cmd = new SqliteCommand(@"
                SELECT h.Bimestre, AVG(h.Frequencia) as MediaFreq 
                FROM Historico h JOIN Alunos a ON h.AlunoId = a.Id 
                WHERE a.Turma = $turma GROUP BY h.Bimestre ORDER BY h.Bimestre", connection))
            {
                cmd.Parameters.AddWithValue("$turma", turma);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    frequenciaMensal.Add(new { month = reader.GetInt32(0) + "º Bim", frequency = Math.Round(reader.GetFloat(1), 1) });
                }
            }

            int ex = 0, bom = 0, reg = 0, ins = 0;
            using (var cmd = new SqliteCommand(@"SELECT COALESCE((SELECT Nota FROM Historico h WHERE h.AlunoId = a.Id ORDER BY Bimestre DESC LIMIT 1), 0) 
                FROM Alunos a WHERE a.Turma = $turma", connection))
            {
                cmd.Parameters.AddWithValue("$turma", turma);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    float n = reader.GetFloat(0);
                    if (n >= 9) ex++; else if (n >= 7) bom++; else if (n >= 5) reg++; else ins++;
                }
            }
            int t = ex + bom + reg + ins;
            var distribuicao = t == 0 ? new List<object>() : new List<object> {
                new { name = "Excelente (9-10)", value = (ex * 100) / t, color = "#22c55e" },
                new { name = "Bom (7-8)", value = (bom * 100) / t, color = "#3b82f6" },
                new { name = "Regular (5-6)", value = (reg * 100) / t, color = "#f59e0b" },
                new { name = "Insuficiente (0-4)", value = (ins * 100) / t, color = "#ef4444" }
            };

            return new { alunos, topRisco, frequenciaMensal, distribuicao };
        }
    }
}