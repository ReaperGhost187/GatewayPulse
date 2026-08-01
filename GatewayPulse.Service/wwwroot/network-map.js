(function (root, factory) {
  const api = factory();
  if (typeof module === 'object' && module.exports) module.exports = api;
  else root.NetworkMap = api;
})(typeof globalThis !== 'undefined' ? globalThis : this, function () {
  'use strict';

  const DEFAULT_MAP_URL = 'https://cms.winlink.org:444/maps/WinlinkGateways.aspx';
  const LEGACY_DRUPAL_MAP_URL = 'https://winlink.org/RMSChannels';

  function defaults() {
    return {
      serviceCode: '',
      rememberServiceCode: true,
      autoRefresh: true,
      autoRefreshMinutes: 15,
      autoOpenInBrowser: true,
      mapUrl: DEFAULT_MAP_URL
    };
  }

  function asBool(value, fallback) {
    if (typeof value === 'boolean') return value;
    if (value === 'true') return true;
    if (value === 'false') return false;
    return fallback;
  }

  function normalize(input) {
    const baseline = defaults();
    const source = input || {};
    const minutes = Number(
      Object.prototype.hasOwnProperty.call(source, 'autoRefreshMinutes')
        ? source.autoRefreshMinutes
        : source.AutoRefreshMinutes);
    let autoRefreshMinutes = Number.isFinite(minutes) ? Math.round(minutes) : baseline.autoRefreshMinutes;
    if (autoRefreshMinutes < 1) autoRefreshMinutes = 1;
    if (autoRefreshMinutes > 180) autoRefreshMinutes = 180;

    const remember = asBool(
      Object.prototype.hasOwnProperty.call(source, 'rememberServiceCode')
        ? source.rememberServiceCode
        : source.RememberServiceCode,
      true);
    const serviceCode = String(
      Object.prototype.hasOwnProperty.call(source, 'serviceCode')
        ? source.serviceCode
        : (source.ServiceCode ?? '')
    ).trim().toUpperCase();

    const mapUrlRaw = String(
      Object.prototype.hasOwnProperty.call(source, 'mapUrl')
        ? source.mapUrl
        : (source.MapUrl ?? baseline.mapUrl)
    ).trim();

    return {
      serviceCode,
      rememberServiceCode: remember,
      autoRefresh: asBool(
        Object.prototype.hasOwnProperty.call(source, 'autoRefresh')
          ? source.autoRefresh
          : source.AutoRefresh,
        true),
      autoRefreshMinutes,
      autoOpenInBrowser: asBool(
        Object.prototype.hasOwnProperty.call(source, 'autoOpenInBrowser')
          ? source.autoOpenInBrowser
          : source.AutoOpenInBrowser,
        true),
      mapUrl: (!mapUrlRaw || isLegacyDrupalMapUrl(mapUrlRaw)) ? DEFAULT_MAP_URL : mapUrlRaw
    };
  }

  function isLegacyDrupalMapUrl(mapUrl) {
    try {
      const url = new URL(String(mapUrl || '').trim());
      return url.hostname.toLowerCase() === 'winlink.org' &&
        url.pathname.replace(/\/$/, '').toLowerCase() === '/rmschannels';
    } catch {
      return false;
    }
  }

  function buildMapUrl(mapUrl, serviceCode) {
    let base = String(mapUrl || DEFAULT_MAP_URL).trim() || DEFAULT_MAP_URL;
    if (isLegacyDrupalMapUrl(base)) base = DEFAULT_MAP_URL;
    let url;
    try {
      url = new URL(base);
      if (url.protocol !== 'http:' && url.protocol !== 'https:') {
        url = new URL(DEFAULT_MAP_URL);
      }
    } catch {
      url = new URL(DEFAULT_MAP_URL);
    }

    // WinlinkGateways.js reads args["servicecodes"] with a case-sensitive key.
    for (const key of [...url.searchParams.keys()]) {
      if (key.toLowerCase() === 'servicecodes') url.searchParams.delete(key);
    }
    const code = String(serviceCode || '').trim();
    if (code) url.searchParams.set('servicecodes', code);
    return url.toString();
  }

  return { DEFAULT_MAP_URL, LEGACY_DRUPAL_MAP_URL, defaults, normalize, buildMapUrl };
});
