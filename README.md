# CrossV9 Web Interface - MVP

A web interface for testing and demonstrating the CrossV9 compression system.

## Features

- 📤 **File Upload**: Drag & drop or click to upload files for compression
- 📊 **Statistics Dashboard**: View compression statistics and performance metrics
- 📈 **Real-time Results**: See compression ratios, savings, and duration
- 💾 **Download Compressed**: Download compressed files
- 📋 **History**: View recent compression operations

## Usage

### With Docker Compose

The web interface is included in the `dockerDeploy/docker-compose.yml` file.

1. Start all services:
   ```bash
   cd dockerDeploy
   docker compose up -d
   ```

2. Access the web interface:
   ```
   http://localhost:5020
   ```

### Standalone

1. Build and run:
   ```bash
   dotnet build
   dotnet run
   ```

2. Update `appsettings.json` to point to Cross service:
   ```json
   "CrossService": {
     "Url": "http://localhost:5000"
   }
   ```

3. Access at: `http://localhost:5000`

## API Endpoints

- `POST /api/compression/compress` - Compress a file
- `POST /api/compression/decompress` - Decompress a file (when implemented)
- `GET /api/compression/statistics` - Get compression statistics
- `GET /api/compression/statistics/summary` - Get summary statistics

## Statistics

The interface tracks:
- Total compressions
- Average compression ratio
- Total space saved
- Average compression duration
- Individual compression history

Statistics are stored in-memory (will be lost on restart). For production, consider using a database.






