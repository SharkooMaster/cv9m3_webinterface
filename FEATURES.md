# New Features

## 1. Storage Comparison Graph

A visual chart showing the comparison between:
- **Original Storage**: How much data would be stored without compression
- **Compressed Storage (GCS Chunks)**: How much data is actually stored in GCS after compression and deduplication

The chart is implemented using Chart.js and updates automatically when statistics are refreshed.

### Features:
- Bar chart visualization
- Automatic updates
- Formatted byte display
- Shows storage savings visually

## 2. Directory Compression

A new tab in the web interface that allows you to:
- Select multiple files or a directory
- Compress all files recursively
- View progress and results

### Features:
- Multiple file selection using directory input
- Batch compression
- Progress tracking
- File list preview
- Summary statistics for all files
- Individual file results

### API Endpoint:
- `POST /api/directory/compress-files` - Compress multiple files
  - Accepts: `files` (List<IFormFile>), `outputDirectory` (optional)
  - Returns: Directory compression summary with individual file results

### UI Features:
- Tab interface (Single File / Directory)
- File list display
- Progress bar
- Results summary
- Error handling per file

## Technical Details

### Statistics Tracking:
- Added `TotalChunksStored` and `TotalChunkBytes` to `StatisticsSummary`
- Estimates chunk storage based on compression ratios
- Tracks unique chunks stored in GCS

### Chart Implementation:
- Uses Chart.js 4.4.0 (CDN)
- Responsive design
- Tooltips show formatted bytes
- Y-axis scales automatically

### Directory Compression:
- Uses HTML5 `multiple` and `webkitdirectory` attributes
- Supports recursive directory selection
- Processes files sequentially with progress updates
- Saves compressed files to server temp directory (can be configured for custom output)

## Usage

1. **Storage Graph**: 
   - Automatically displays when you visit the statistics page
   - Updates when you compress files or refresh statistics

2. **Directory Compression**:
   - Click "Directory" tab
   - Click upload area to select files/folder
   - Select multiple files or a directory
   - Click "Compress All Files"
   - View results and download compressed files

## Notes

- The storage graph estimates chunk storage based on compression ratios
- Directory compression saves to server's temporary directory by default
- For production, configure output directory or download mechanism
- Chart updates in real-time as you compress files






