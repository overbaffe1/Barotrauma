const WebSocket = require('ws');
const http = require('http');
const fs = require('fs');
const path = require('path');

// --- Simple HTTP server serving the HTML page ---
const server = http.createServer((req, res) => {
  if (req.url === '/' || req.url === '/index.html') {
    fs.readFile(path.join(__dirname, 'index.html'), (err, data) => {
      if (err) {
        res.writeHead(500);
        res.end('Error loading index.html');
        return;
      }
      res.writeHead(200, { 'Content-Type': 'text/html' });
      res.end(data);
    });
  } else {
    res.writeHead(404);
    res.end('Not found');
  }
});

let viewerCount = 0;
const clients = new Set();

const wss = new WebSocket.Server({ server });

wss.on('connection', (ws) => {
  clients.add(ws);
  viewerCount++;
  broadcastViewerCount();

  ws.on('message', (msg) => {
    const text = msg.toString();
    // Broadcast message to all other clients (broadcaster can read)
    for (const client of clients) {
      if (client !== ws && client.readyState === WebSocket.OPEN) {
        client.send(text);
      }
    }
    // Also echo back to sender maybe?
    ws.send(`Echo: ${text}`);
  });

  ws.on('close', () => {
    clients.delete(ws);
    viewerCount = Math.max(0, viewerCount - 1);
    broadcastViewerCount();
  });
});

function broadcastViewerCount() {
  const msg = JSON.stringify({ type: 'viewerCount', count: viewerCount });
  for (const client of clients) {
    if (client.readyState === WebSocket.OPEN) {
      client.send(msg);
    }
  }
}

// Start server on port 3000
server.listen(3000, () => {
  console.log('Stream server running on http://localhost:3000');
});
