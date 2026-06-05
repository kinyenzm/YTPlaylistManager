const PROXY_CONFIG = [
  {
    context: ["/api"],
    target: "http://localhost:5080",
    secure: false,
  },
];

module.exports = PROXY_CONFIG;
