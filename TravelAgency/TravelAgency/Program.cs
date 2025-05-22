using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using TravelAgency.Models;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();

var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");

// 1. GET /api/trips - w tym endpoint-cie pobieramy wszystkie dostępne wcieczki z ich informacjami
app.MapGet("/api/trips", async () =>
{
    var trips = new List<Trip>();

    using var conn = new SqlConnection(connectionString);
    await conn.OpenAsync();

    // Pobieranie listy wszystkich wycieczek wraz z przypisanymi krajami
    var cmd = new SqlCommand(@"
        SELECT t.IdTrip, t.Name, t.Description, t.DateFrom, t.DateTo, t.MaxPeople, c.Name AS Country
        FROM Trip t
        LEFT JOIN Country_Trip ct ON ct.IdTrip = t.IdTrip
        LEFT JOIN Country c ON c.IdCountry = ct.IdCountry
    ", conn);

    using var reader = await cmd.ExecuteReaderAsync();

    var tripDict = new Dictionary<int, Trip>();

    while (await reader.ReadAsync())
    {
        var id = (int)reader["IdTrip"];
        if (!tripDict.ContainsKey(id))
        {
            tripDict[id] = new Trip
            {
                TripId = id,
                Name = reader["Name"].ToString(),
                Description = reader["Description"].ToString(),
                DateFrom = (DateTime)reader["DateFrom"],
                DateTo = (DateTime)reader["DateTo"],
                MaxPeople = (int)reader["MaxPeople"]
            };
        }

        if (!reader.IsDBNull(reader.GetOrdinal("Country")))
            tripDict[id].Countries.Add(reader["Country"].ToString());
    }

    return Results.Ok(tripDict.Values);
});

// 2. GET /api/clients/{id}/trips - w tym endpoint-cie pobieramy listę wszystkich wycieczek powiązanych z podanym klientem
app.MapGet("/api/clients/{id}/trips", async (int id) =>
{
    using var conn = new SqlConnection(connectionString);
    await conn.OpenAsync();

    // Sprawdzamy czy klient istnieje
    var checkClient = new SqlCommand("SELECT 1 FROM Client WHERE IdClient = @id", conn);
    checkClient.Parameters.AddWithValue("@id", id);
    if (await checkClient.ExecuteScalarAsync() == null)
        return Results.NotFound("Client not found.");

    // Pobieranie listy wszystkich wycieczek przypisanych do podanego klienta
    var cmd = new SqlCommand(@"
        SELECT t.IdTrip, t.Name, t.Description, t.DateFrom, t.DateTo, t.MaxPeople,
               ct.RegisteredAt, ct.PaymentDate
        FROM Trip t
        JOIN Client_Trip ct ON ct.IdTrip = t.IdTrip
        WHERE ct.IdClient = @id", conn);
    cmd.Parameters.AddWithValue("@id", id);

    var trips = new List<object>();
    var reader = await cmd.ExecuteReaderAsync();

    while (await reader.ReadAsync())
    {
        trips.Add(new
        {
            IdTrip = (int)reader["IdTrip"],
            Name = reader["Name"].ToString(),
            Description = reader["Description"].ToString(),
            DateFrom = (DateTime)reader["DateFrom"],
            DateTo = (DateTime)reader["DateTo"],
            MaxPeople = (int)reader["MaxPeople"],
            RegisteredAt = (int)reader["RegisteredAt"],
            PaymentDate = reader["PaymentDate"] == DBNull.Value ? null : (int?)reader["PaymentDate"]
        });
    }

    return Results.Ok(trips);
});

// 3. POST /api/clients - w tym endpoint-cie dodajemy nowego klienta na podstawie otrzymanych danych
app.MapPost("/api/clients", async ([FromBody] Client client) =>
{
    // Zwracamy informację jeżeli niepełne dane
    if (string.IsNullOrWhiteSpace(client.FirstName) ||
        string.IsNullOrWhiteSpace(client.LastName) ||
        string.IsNullOrWhiteSpace(client.Email) ||
        string.IsNullOrWhiteSpace(client.Pesel))
        return Results.BadRequest("All fields are required.");

    using var conn = new SqlConnection(connectionString);
    await conn.OpenAsync();

    // Tworzymy nowego klienta na podstawie otrzymanych danych
    var cmd = new SqlCommand(@"
        INSERT INTO Client (FirstName, LastName, Email, Telephone, Pesel)
        OUTPUT INSERTED.IdClient
        VALUES (@fn, @ln, @em, @tel, @pesel)
    ", conn);

    cmd.Parameters.AddWithValue("@fn", client.FirstName);
    cmd.Parameters.AddWithValue("@ln", client.LastName);
    cmd.Parameters.AddWithValue("@em", client.Email);
    cmd.Parameters.AddWithValue("@tel", client.Telephone ?? (object)DBNull.Value);
    cmd.Parameters.AddWithValue("@pesel", client.Pesel);

    var newId = (int)await cmd.ExecuteScalarAsync();

    return Results.Created($"/api/clients/{newId}", new { IdClient = newId });
});

// 4. PUT /api/clients/{id}/trips/{tripId} - w tym endpoint-cie zapisujemy podanego klienta na podaną wycieczkę
app.MapPut("/api/clients/{id}/trips/{tripId}", async (int id, int tripId) =>
{
    using var conn = new SqlConnection(connectionString);
    await conn.OpenAsync();

    // Sprawdzamy czy klient istnieje
    var checkClient = new SqlCommand("SELECT 1 FROM Client WHERE IdClient = @id", conn);
    checkClient.Parameters.AddWithValue("@id", id);
    if (await checkClient.ExecuteScalarAsync() == null)
        return Results.NotFound("Client not found.");

    // Sprawdzamy czy wycieczka istnieje
    var checkTrip = new SqlCommand("SELECT MaxPeople FROM Trip WHERE IdTrip = @tripId", conn);
    checkTrip.Parameters.AddWithValue("@tripId", tripId);
    var maxPeople = await checkTrip.ExecuteScalarAsync();
    if (maxPeople == null)
        return Results.NotFound("Trip not found.");

    // Sprawdzamy liczbę uczestników
    var countCmd = new SqlCommand("SELECT COUNT(*) FROM Client_Trip WHERE IdTrip = @tripId", conn);
    countCmd.Parameters.AddWithValue("@tripId", tripId);
    var currentCount = (int)(await countCmd.ExecuteScalarAsync());
    if (currentCount >= (int)maxPeople)
        return Results.BadRequest("Maximum number of participants reached.");

    // Sprawdzamy czy już zapisany
    var existsCmd = new SqlCommand("SELECT 1 FROM Client_Trip WHERE IdClient = @id AND IdTrip = @tripId", conn);
    existsCmd.Parameters.AddWithValue("@id", id);
    existsCmd.Parameters.AddWithValue("@tripId", tripId);
    if (await existsCmd.ExecuteScalarAsync() != null)
        return Results.Conflict("Client is already registered for this trip.");

    // Zapisujemy rejestrację na wycieczkę
    int registeredAt = int.Parse(DateTime.Now.ToString("yyyyMMdd"));
    var insertCmd = new SqlCommand(@"
        INSERT INTO Client_Trip (IdClient, IdTrip, RegisteredAt)
        VALUES (@id, @tripId, @registeredAt)", conn);
    insertCmd.Parameters.AddWithValue("@id", id);
    insertCmd.Parameters.AddWithValue("@tripId", tripId);
    insertCmd.Parameters.AddWithValue("@registeredAt", registeredAt);

    await insertCmd.ExecuteNonQueryAsync();

    return Results.Ok("Client registered for trip.");
});

// 5. DELETE /api/clients/{id}/trips/{tripId} - w tym endpoint-cie wypisujemy podanego klienta z podanej wycieczki
app.MapDelete("/api/clients/{id}/trips/{tripId}", async (int id, int tripId) =>
{
    using var conn = new SqlConnection(connectionString);
    await conn.OpenAsync();

    // Sprawdzamy czy zapis istnieje
    var checkCmd = new SqlCommand(@"
        SELECT COUNT(*) FROM Client_Trip WHERE IdClient = @id AND IdTrip = @tripId
    ", conn);
    checkCmd.Parameters.AddWithValue("@id", id);
    checkCmd.Parameters.AddWithValue("@tripId", tripId);

    if ((int)await checkCmd.ExecuteScalarAsync() == 0)
        return Results.NotFound("Registration not found.");

    // Wypisujemy klienta z wycieczki
    var deleteCmd = new SqlCommand(@"
        DELETE FROM Client_Trip WHERE IdClient = @id AND IdTrip = @tripId
    ", conn);
    deleteCmd.Parameters.AddWithValue("@id", id);
    deleteCmd.Parameters.AddWithValue("@tripId", tripId);

    await deleteCmd.ExecuteNonQueryAsync();

    return Results.Ok("Client unregistered from trip.");
});

app.Run();