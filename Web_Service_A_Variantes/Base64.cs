using System;
using System.Data.SqlClient;
using System.IO;

public class ImageToBase64Handler
{
    private string connectionString = "Server=localhost;Database=MiBaseDatos;Integrated Security=true;";

    // Método para convertir imagen desde archivo a Base64
    public string ConvertImageToBase64(string imagePath)
    {
        try
        {
            byte[] imageBytes = File.ReadAllBytes(imagePath);
            return Convert.ToBase64String(imageBytes);
        }
        catch (Exception ex)
        {
            throw new Exception($"Error al convertir imagen a Base64: {ex.Message}");
        }
    }

    // Método para convertir imagen desde byte array a Base64
    public string ConvertImageToBase64(byte[] imageBytes)
    {
        try
        {
            return Convert.ToBase64String(imageBytes);
        }
        catch (Exception ex)
        {
            throw new Exception($"Error al convertir imagen a Base64: {ex.Message}");
        }
    }

    // Método para guardar imagen Base64 en SQL Server
    public bool SaveImageToDatabase(int id, string base64Image, string fileName = null)
    {
        string query = @"
            INSERT INTO Imagenes (Id, ImagenBase64, NombreArchivo, FechaCreacion) 
            VALUES (@Id, @ImagenBase64, @NombreArchivo, @FechaCreacion)";

        try
        {
            using (SqlConnection connection = new SqlConnection(connectionString))
            {
                using (SqlCommand command = new SqlCommand(query, connection))
                {
                    command.Parameters.AddWithValue("@Id", id);
                    command.Parameters.AddWithValue("@ImagenBase64", base64Image);
                    command.Parameters.AddWithValue("@NombreArchivo", fileName ?? (object)DBNull.Value);
                    command.Parameters.AddWithValue("@FechaCreacion", DateTime.Now);

                    connection.Open();
                    int result = command.ExecuteNonQuery();
                    return result > 0;
                }
            }
        }
        catch (Exception ex)
        {
            throw new Exception($"Error al guardar en base de datos: {ex.Message}");
        }
    }

    // Método para actualizar imagen existente
    public bool UpdateImageInDatabase(int id, string base64Image)
    {
        string query = @"
            UPDATE Imagenes 
            SET ImagenBase64 = @ImagenBase64, FechaModificacion = @FechaModificacion 
            WHERE Id = @Id";

        try
        {
            using (SqlConnection connection = new SqlConnection(connectionString))
            {
                using (SqlCommand command = new SqlCommand(query, connection))
                {
                    command.Parameters.AddWithValue("@Id", id);
                    command.Parameters.AddWithValue("@ImagenBase64", base64Image);
                    command.Parameters.AddWithValue("@FechaModificacion", DateTime.Now);

                    connection.Open();
                    int result = command.ExecuteNonQuery();
                    return result > 0;
                }
            }
        }
        catch (Exception ex)
        {
            throw new Exception($"Error al actualizar en base de datos: {ex.Message}");
        }
    }

    // Método para recuperar imagen desde la base de datos
    public string GetImageFromDatabase(int id)
    {
        string query = "SELECT ImagenBase64 FROM Imagenes WHERE Id = @Id";

        try
        {
            using (SqlConnection connection = new SqlConnection(connectionString))
            {
                using (SqlCommand command = new SqlCommand(query, connection))
                {
                    command.Parameters.AddWithValue("@Id", id);

                    connection.Open();
                    object result = command.ExecuteScalar();
                    return result?.ToString();
                }
            }
        }
        catch (Exception ex)
        {
            throw new Exception($"Error al recuperar imagen: {ex.Message}");
        }
    }

    // Método para convertir Base64 de vuelta a imagen y guardar archivo
    public bool SaveBase64ToImageFile(string base64String, string outputPath)
    {
        try
        {
            byte[] imageBytes = Convert.FromBase64String(base64String);
            File.WriteAllBytes(outputPath, imageBytes);
            return true;
        }
        catch (Exception ex)
        {
            throw new Exception($"Error al guardar imagen desde Base64: {ex.Message}");
        }
    }

    // Ejemplo de uso completo
    public void EjemploCompleto()
    {
        try
        {
            // 1. Convertir imagen a Base64
            string rutaImagen = @"C:\mi_imagen.jpg";
            string imagenBase64 = ConvertImageToBase64(rutaImagen);

            // 2. Guardar en base de datos
            bool guardado = SaveImageToDatabase(1, imagenBase64, "mi_imagen.jpg");

            if (guardado)
            {
                Console.WriteLine("Imagen guardada exitosamente");

                // 3. Recuperar imagen de la base de datos
                string imagenRecuperada = GetImageFromDatabase(1);

                // 4. Convertir de vuelta a archivo
                if (!string.IsNullOrEmpty(imagenRecuperada))
                {
                    SaveBase64ToImageFile(imagenRecuperada, @"C:\imagen_recuperada.jpg");
                    Console.WriteLine("Imagen recuperada y guardada");
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error: {ex.Message}");
        }
    }
}

// Script SQL para crear la tabla
/*
CREATE TABLE Imagenes (
    Id INT PRIMARY KEY,
    ImagenBase64 NVARCHAR(MAX) NOT NULL,
    NombreArchivo NVARCHAR(255),
    FechaCreacion DATETIME2 DEFAULT GETDATE(),
    FechaModificacion DATETIME2 NULL
);
*/

// Ejemplo alternativo usando FileUpload en Windows Forms
public class WindowsFormsExample
{
    public string ConvertFileUploadToBase64(string filePath)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException("El archivo no existe");

        // Validar que sea una imagen
        string extension = Path.GetExtension(filePath).ToLower();
        string[] validExtensions = { ".jpg", ".jpeg", ".png", ".gif", ".bmp" };

        if (!Array.Exists(validExtensions, ext => ext == extension))
            throw new ArgumentException("El archivo no es una imagen válida");

        return Convert.ToBase64String(File.ReadAllBytes(filePath));
    }
}

//// Ejemplo para ASP.NET con IFormFile
//public class AspNetExample
//{
//    public async Task<string> ConvertIFormFileToBase64(Microsoft.AspNetCore.Http.IFormFile file)
//    {
//        if (file == null || file.Length == 0)
//            throw new ArgumentException("No se ha seleccionado ningún archivo");

//        using (var memoryStream = new MemoryStream())
//        {
//            await file.CopyToAsync(memoryStream);
//            byte[] fileBytes = memoryStream.ToArray();
//            return Convert.ToBase64String(fileBytes);
//        }
//    }
//}