import fs from 'node:fs';
import { Tokenizer } from '../evidence/tokenizers/package/dist/tokenizers.mjs';
const tokenizerJson=JSON.parse(fs.readFileSync('assets_cache/clip-vit-base-patch32-tokenizer.json','utf8'));
const config=JSON.parse(fs.readFileSync('evidence/tokenizer_config.json','utf8').replace(/^\uFEFF/,''));
const reference=new Tokenizer(tokenizerJson,config);
const actual=JSON.parse(fs.readFileSync('evidence/probe/tokens.json','utf8'));
const results=actual.map(({prompt,ids})=>{
  const expected=reference.encode(prompt).ids.slice(0,77);
  return {prompt,expected,actual:ids,pass:JSON.stringify(ids)===JSON.stringify(expected)};
});
const report={reference:'@huggingface/tokenizers 0.2.0 (official independent JavaScript implementation)',cases:results.length,passed:results.filter(r=>r.pass).length,results};
fs.writeFileSync('evidence/probe/tokenizer-parity.json',JSON.stringify(report,null,2));
console.log(JSON.stringify({cases:report.cases,passed:report.passed,failures:results.filter(r=>!r.pass)},null,2));
if(report.passed!==report.cases)process.exitCode=1;
