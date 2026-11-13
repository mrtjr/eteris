using Eteris.Browser.Watching;

namespace Eteris.Browser.Tests;

// O preço que a página declara nos dados estruturados (JSON-LD, itemprop="price", meta product:price:amount) vale mais do
// que qualquer número do texto: as prestações e o «de … por …» não o enganam.
public class StructuredPriceTests
{
    [Fact] public void JsonLd_ProductOffer() =>
        Assert.Equal(new PriceReading(1349.9m, "BRL", "ld"),
            StructuredPrice.From(["""{"@type":"Product","offers":{"@type":"Offer","price":"1349.9","priceCurrency":"BRL"}}"""], [], [], null));

    [Fact] public void JsonLd_GraphArrayAndAggregate() =>
        Assert.Equal(588.22m, StructuredPrice.From(["""{"@graph":[{"@type":"WebPage"},{"@type":"Product","offers":[{"@type":"AggregateOffer","lowPrice":588.22,"priceCurrency":"BRL"}]}]}"""], [], [], null)!.Value);

    [Fact]
    public void Itemprop_ThenMeta_WhenNoJsonLd()
    {
        Assert.Equal(907.99m, StructuredPrice.From([], ["907.99"], [], "BRL")!.Value);
        Assert.Equal(12.5m, StructuredPrice.From([], [], ["12.50"], "EUR")!.Value);
    }

    [Fact] public void BrokenJsonLd_IsIgnored() => Assert.Null(StructuredPrice.From(["{not json"], [], [], null));

    // Um bloco partido não esconde o bom que vem a seguir.
    [Fact] public void BrokenJsonLd_DoesNotHideTheNextBlock() =>
        Assert.Equal(19.9m, StructuredPrice.From(["{not json", """[{"@type":"Product","offers":{"price":19.9}}]"""], [], [], null)!.Value);

    // Vários vendedores: conta o mais baixo, para que a mesma lista noutra ordem nunca pareça uma descida.
    [Fact]
    public void SeveralOffers_TakeTheLowest_WhateverTheOrder()
    {
        Assert.Equal(90m, StructuredPrice.From(["""{"@type":"Product","offers":[{"price":"100"},{"price":"90"}]}"""], [], [], null)!.Value);
        Assert.Equal(90m, StructuredPrice.From(["""{"@type":"Product","offers":[{"price":"90"},{"price":"100"}]}"""], [], [], null)!.Value);
    }

    // A priceSpecification também traz portes, taxas de pagamento e preços de sócio: só conta quando a oferta não tem
    // price nem lowPrice, senão os portes (9,90) passavam pelo preço.
    [Fact]
    public void PriceSpecification_OnlyWhenTheOfferHasNoPrice()
    {
        Assert.Equal(588.22m, StructuredPrice.From(["""{"@type":"Product","offers":{"price":"588.22","priceSpecification":{"@type":"DeliveryChargeSpecification","price":"9.90"}}}"""], [], [], null)!.Value);
        Assert.Equal(500m, StructuredPrice.From(["""{"@type":"Product","offers":{"lowPrice":"500","priceSpecification":[{"price":"1"}]}}"""], [], [], null)!.Value);
        Assert.Equal(70m, StructuredPrice.From(["""{"@type":"Product","offers":[{"price":"80"},{"priceSpecification":{"price":"70"}}]}"""], [], [], null)!.Value);
    }

    // «0.00» numa página esgotada não é um preço (seria uma descida falsa).
    [Fact]
    public void ZeroPrices_AreIgnored()
    {
        Assert.Null(StructuredPrice.From(["""{"@type":"Product","offers":{"price":"0.00","priceCurrency":"BRL"}}"""], ["0"], ["0.00"], "BRL"));
        Assert.Equal(5m, StructuredPrice.From(["""{"@type":"Product","offers":{"price":0}}"""], ["5"], [], null)!.Value);
    }

    // A moeda: a declarada; senão o símbolo no próprio valor; senão a da página.
    [Fact]
    public void Currency_DeclaredThenSymbolThenHint()
    {
        Assert.Equal("BRL", StructuredPrice.From(["""{"@type":"Product","offers":{"price":"10","priceCurrency":"brl"}}"""], [], [], "EUR")!.Currency);
        Assert.Equal(new PriceReading(907.99m, "BRL", "itemprop"), StructuredPrice.From([], ["R$ 907,99"], [], null));
        Assert.Equal("EUR", StructuredPrice.From(["""{"@type":"Product","offers":{"price":"10"}}"""], [], [], "EUR")!.Currency);
        Assert.Null(StructuredPrice.From(["""{"@type":"Product","offers":{"price":"10"}}"""], [], [], null)!.Currency);
    }

    // O JSON-LD escreve o preço com ponto decimal, como número ou como texto; o texto com separadores também se lê, e o
    // preço pode vir numa priceSpecification.
    [Fact]
    public void JsonLd_NumberAndTextForms()
    {
        Assert.Equal(1299.9m, StructuredPrice.From(["""{"@type":"Product","offers":{"price":1299.90}}"""], [], [], null)!.Value);
        Assert.Equal(1299.9m, StructuredPrice.From(["""{"@type":"Product","offers":{"price":"1.299,90"}}"""], [], [], null)!.Value);
        Assert.Equal(49.5m, StructuredPrice.From(["""{"@type":["Product","Thing"],"offers":{"priceSpecification":{"price":"49.50"}}}"""], [], [], null)!.Value);
    }
}
